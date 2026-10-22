using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;
using TradingApp.Infrastructure.Enums;
using TradingApp.Infrastructure.Helpers.Agentic;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.Agentic;
using TradingApp.Infrastructure.Interfaces.ConversationMemory;
using TradingApp.Infrastructure.Interfaces.Retrieval;
using TradingApp.Infrastructure.Models.Agentic;
using TradingApp.Infrastructure.Models.ConversationMemory;
using TradingApp.Infrastructure.Models.Ingestion;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Services.Agentic
{
    public class ToolDispatcherService : IToolDispatcherService
    {
        private readonly ILogger<ToolDispatcherService> _logger;
        private readonly IFileDebugLogger _fileDebugLogger;
        private readonly IQueryDecompositionService _queryDecompositionService;
        private readonly IContextRetrievalService _contextRetrievalService;
        private readonly IConversationReuseService _conversationReuseService;
        private readonly IFileExpansionService _fileExpansionService;
        private readonly ICorpusManifestService _corpusManifestService;

        public ToolDispatcherService
        (
            ILogger<ToolDispatcherService> logger,
            IFileDebugLogger fileDebugLogger,
            IQueryDecompositionService queryDecompositionService,
            IContextRetrievalService contextRetrievalService,
            IConversationReuseService conversationResuseService,
            IFileExpansionService fileExpansionService,
            ICorpusManifestService corpusManifestService
        )
        {
            _logger = logger;
            _fileDebugLogger = fileDebugLogger;
            _queryDecompositionService = queryDecompositionService;
            _contextRetrievalService = contextRetrievalService;
            _conversationReuseService = conversationResuseService;
            _fileExpansionService = fileExpansionService;
            _corpusManifestService = corpusManifestService;
        }

        public async Task<ToolDispatchResponse> DispatchToolCallAsync(ToolDispatchRequest request)
        {
            var context = request.Context;
            var toolUseBlock = request.ToolUseBlock;
            var isKnownTool = request.IsKnownTool;
            var toolName = request.ToolName;
            string toolTextResult;
            var toolDispatchResponse = new ToolDispatchResponse();
            CancellationToken cancellationToken = request.Context.Request.CancellationToken;

            try
            {
                if (!isKnownTool)
                {
                    toolTextResult = $"Unknown tool: {toolUseBlock.Name}";

                    toolDispatchResponse.IsError = true;

                    _logger.LogWarning("RunAgenticLoopAsync | UnknownToolCalled:{ToolName}", toolUseBlock.Name);
                    await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", 
                        $"[full-response][ApiCall #{context.ApiCallNumber}] Unknown tool called: {toolUseBlock.Name}", toolUseBlock.Input);
                }
                else if (toolName == AgenticTool.search_knowledge_base && Interlocked.Increment(ref context.Counters.SearchKnowledgeBaseToolCounter) > 3)
                {
                    Interlocked.Decrement(ref context.Counters.SearchKnowledgeBaseToolCounter);

                    toolTextResult = SystemPromptBuilder.SearchCapReachedSentinel;

                    await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                        $"[full-response][ApiCall #{context.ApiCallNumber}] Search capped — consecutive empty limit reached", toolUseBlock.Input);
                }
                else
                {
                    var toolExecutionContext = new ToolExecutionContext
                    {
                        ConversationId = context.Request.ConversationId,
                        IsNewConversation = context.Request.IsNewConversation,
                        ToolUseId = toolUseBlock.ID,
                        ToolCallNumber = Interlocked.Increment(ref context.Counters.ToolCallNumber),
                        ToolName = toolName,
                        Input = toolUseBlock.Input,
                        SeenChunkKeysLock = context.SeenChunkKeysLock,
                        SeenChunkKeys = context.SeenChunkKeys,
                        ChunksByToolUseIdLock = context.ChunksByToolUseIdLock,
                        ChunksByToolUseId = context.ChunksByToolUseId,
                        ApiCallNumber = context.ApiCallNumber
                    };

                    toolDispatchResponse.CurrentToolCallNumber = toolExecutionContext.ToolCallNumber;

                    bool shouldSkipGetFullFileToolCall = false;
                    bool manifestSaysNotIndexed = false;
                    bool chunksAlreadyReconstructFile = false;

                    if (toolName == AgenticTool.get_full_file)
                    {
                        manifestSaysNotIndexed = IsGetFullFileToolCallWasteful(toolExecutionContext, context.Messages);
                        chunksAlreadyReconstructFile = IsFullFileAlreadyReconstructedFromChunks(toolExecutionContext);

                        shouldSkipGetFullFileToolCall = manifestSaysNotIndexed || chunksAlreadyReconstructFile;
                    }

                    if (shouldSkipGetFullFileToolCall)
                    {
                        var fileName = toolUseBlock.Input.TryGetValue("fileName", out var fileNameValue) ? fileNameValue.ToString() : string.Empty;
                        toolTextResult = $"{SystemPromptBuilder.NoFullFileContentAvailableSentinel} {fileName}.";

                        var reason = manifestSaysNotIndexed ? "manifest shows not fully indexed" : "chunks already reconstruct the full file";

                        await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                            $"[full-response][ApiCall #{context.ApiCallNumber}] get_full_file intercepted — reason:", reason);
                    }
                    else
                    {
                        toolTextResult = await ExecuteToolAsync(toolExecutionContext, cancellationToken);
                    }
                }

                toolDispatchResponse.Text = toolTextResult;
                return toolDispatchResponse;
            }
            catch(OperationCanceledException ocEx) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(ocEx, "ToolCallCancelled | ToolId: {ToolId} | ToolName: {ToolName} | ApiCallNumber: {ApiCallNumber}",
                    toolUseBlock.ID, toolName, context.ApiCallNumber);
                throw;
            }
            catch (Exception ex)
            {
                toolTextResult = $"{SystemPromptBuilder.ToolTextResultGenerationErrorMessageSentinel} {toolUseBlock.ID}";

                toolDispatchResponse.IsError = true;

                _logger.LogWarning(ex, "get_full_file tool call skipped | UnableToResolveFileName " +
                    "| ToolUseID:{ToolUseID}", toolUseBlock.ID);

                await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"[full-response][ApiCall #{context.ApiCallNumber}]" +
                    $" Result from tool #{toolDispatchResponse.CurrentToolCallNumber}: {toolName}", toolTextResult);

                toolDispatchResponse.Text = toolTextResult;
                return toolDispatchResponse;
            }
        }

        private async Task<string> ExecuteToolAsync(ToolExecutionContext context, CancellationToken ct)
        {
            _logger.LogInformation("RunAgenticLoopAsync | ToolCalled:{ToolName}", context.ToolName);
            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                $"[full-response][ApiCall #{context.ApiCallNumber}] Tool #{context.ToolCallNumber} called: {context.ToolName}", context.Input);

            string result;

            switch (context.ToolName)
            {
                case AgenticTool.decompose_query:
                    {
                        result = await HandleDecomposeQueryAsync(context.Input, ct);
                        break;
                    }
                case AgenticTool.search_knowledge_base:
                    {
                        result = await HandleSearchKnowledgeBaseAsync(context, ct);
                        break;
                    }
                case AgenticTool.get_full_file:
                    {
                        result = await HandleGetFullFileAsync(context, ct);
                        break;
                    }
                case AgenticTool.get_database_context:
                    {
                        result = await HandleGetDatabaseContextAsync(context, ct);
                        break;
                    }
                case AgenticTool.get_all_indexed_files:
                    {
                        result = await HandleGetAllIndexedFileNames(ct);
                        break;
                    }
                default:
                    result = $"Unknown tool: {context.ToolName}";
                    break;
            }

            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"[full-response][ApiCall #{context.ApiCallNumber}]" +
                $" Result from tool #{context.ToolCallNumber}: {context.ToolName}", result);

            return result;
        }

        private async Task<string> HandleDecomposeQueryAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
        {
            string question;

            try
            {
                question = input["question"].GetString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HandleDecomposeQueryAsyncFailed | Input: {Input}", JsonSerializer.Serialize(input));
                question = "";
            }

            var subQueries = await _queryDecompositionService.DecomposeQueryAsync(question, ct);

            if (subQueries.Count == 0) return SystemPromptBuilder.DecomposeQueryInvalidInputSentinel;

            return string.Join("\n", subQueries.Select((subQuery, i) => $"{i + 1}.{subQuery}"));
        }

        private async Task<string> HandleSearchKnowledgeBaseAsync(ToolExecutionContext context, CancellationToken ct)
        {
            string query;

            try
            {
                query = context.Input["query"].GetString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HandleSearchKnowledgeBaseAsyncFailed | Input: {Input}", JsonSerializer.Serialize(context.Input));
                query = "";
            }

            var retrievedChunks = await _contextRetrievalService.RetrieveRelevantChunksAsync(query, ct);

            var newChunks = retrievedChunks.Where(chunk =>
            {
                lock (context.SeenChunkKeysLock)
                {
                    return context.SeenChunkKeys.Add(chunk.Key ?? string.Empty);
                }
            }).ToList();

            var uShapeSortedChunks = ChunkReordering.ReorderChunksToUShape(newChunks);

            ComputeFullFileReconstructableValue(uShapeSortedChunks);

            lock (context.ChunksByToolUseIdLock)
            {
                context.ChunksByToolUseId[context.ToolUseId] = uShapeSortedChunks;
            }

            await _conversationReuseService.TryPersistReusableConversationArtifactsAsync(context.ConversationId, newChunks, []);

            return ChunkResultDeduplication.FormatChunksForToolResult(uShapeSortedChunks);
        }

        private async Task<string> HandleGetFullFileAsync(ToolExecutionContext context, CancellationToken ct)
        {
            string fileName;

            try
            {
                fileName = context.Input["fileName"].GetString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HandleGetFullFileAsyncFailed | Input: {Input}", JsonSerializer.Serialize(context.Input));
                fileName = "";
            }

            var fullFileContent = await _contextRetrievalService.GetFullFileContentAsync(fileName, ct);

            if (string.IsNullOrEmpty(fullFileContent))
            {
                return $"{SystemPromptBuilder.NoFullFileContentAvailableSentinel} {fileName}.";
            }
            else
            {
                Dictionary<string, string> fullFileDictionaryEntry = [];
                fullFileDictionaryEntry.Add(fileName, fullFileContent);
                await _conversationReuseService.TryPersistReusableConversationArtifactsAsync(context.ConversationId, [], fullFileDictionaryEntry);

                return $"FileName: {fileName} \n\n{fullFileContent}";
            }
        }

        private async Task<string> HandleGetDatabaseContextAsync(ToolExecutionContext context, CancellationToken ct)
        {
            string query;

            try
            {
                query = context.Input["query"].GetString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HandleGetDatabaseContextAsyncFailed | Input: {Input}", JsonSerializer.Serialize(context.Input));
                query = "";
            }

            var reusableContextArtifacts = await _conversationReuseService.TryRetrieveReusableConversationArtifactsAsync(
                new ReusableConversationArtifactsRequest
                {
                    ConversationId = context.ConversationId,
                    UserMessage = query,
                    IsNewConversation = context.IsNewConversation
                }, ct);

            if (reusableContextArtifacts.ConversationChunks.Count == 0 && reusableContextArtifacts.ConversationFullFiles.Count == 0)
            {
                return SystemPromptBuilder.NoReusableContextFoundSentinel;
            }

            var mappedChunks = RetrievalResultMapping.ToRetrievedChunks(reusableContextArtifacts.ConversationChunks);

            var newChunks = mappedChunks.Where(chunk =>
            {
                lock (context.SeenChunkKeysLock)
                {
                    return context.SeenChunkKeys.Add(chunk.Key ?? string.Empty);
                }
            }).ToList();

            var fullFileDedupKeys = reusableContextArtifacts.ConversationFullFiles.Select(x => x.SourceFile).ToHashSet();

            var uShapeSortedChunks = ChunkReordering.ReorderChunksToUShape(
                newChunks.Where(chunk => !fullFileDedupKeys.Contains(chunk.SourceFile ?? string.Empty)).ToList());

            var fullFilesMap = await _fileExpansionService.GetExistingFullFileContentsMapAsync(uShapeSortedChunks.Select(x => x.SourceFile), ct);

            foreach (var chunk in uShapeSortedChunks)
            {
                chunk.IsFullFileIndexed = fullFilesMap.ContainsKey(chunk.SourceFile ?? string.Empty);
            }

            lock (context.ChunksByToolUseIdLock)
            {
                context.ChunksByToolUseId[context.ToolUseId] = uShapeSortedChunks;
            }

            ComputeFullFileReconstructableValue(uShapeSortedChunks);

            var formattedChunks = uShapeSortedChunks.Count > 0
                ? ChunkResultDeduplication.FormatChunksForToolResult(uShapeSortedChunks)
                : string.Empty;

            var fullFileContents = RetrievalResultMapping.ToFullFileContents(reusableContextArtifacts.ConversationFullFiles);

            var formattedFullFiles = fullFileContents.Count > 0
                ? string.Join("\n\n", fullFileContents.Select(x => $"FileName: {x.Key} \n\n{x.Value}"))
                : string.Empty;

            var combinedResult = string.Join("\n\n", new[] { formattedChunks, formattedFullFiles }.Where(s => !string.IsNullOrEmpty(s)));

            return string.IsNullOrEmpty(combinedResult) ? SystemPromptBuilder.NoReusableContextFoundSentinel : combinedResult;
        }

        public async Task<string> HandleGetAllIndexedFileNames(CancellationToken ct)
        {
            var corpusManifest = await _corpusManifestService.GetEntireCorpusManifestAsync(ct);

            if (corpusManifest.Count == 0)
            {
                return SystemPromptBuilder.NoSourceFileNamesFoundSentinel;
            }

            return FormatCorpusManifestForToolResult(corpusManifest);
        }

        private static void ComputeFullFileReconstructableValue(List<RetrievedChunk> uShapeSortedChunks)
        {
            if (uShapeSortedChunks.Count == 0) return;

            var fullyReconstructableSourceFilesMap = uShapeSortedChunks
                .GroupBy(x => x.SourceFile)
                .ToDictionary(group => group.Key ?? string.Empty,
                group =>
                {
                    var totalIndexIndices = Enumerable.Range(0, group.First().TotalChunkCount).ToHashSet();
                    var partialIndexIndices = group.Select(x => x.ChunkIndex).ToHashSet();
                    return totalIndexIndices.SetEquals(partialIndexIndices);
                });

            foreach (var chunk in uShapeSortedChunks)
            {
                fullyReconstructableSourceFilesMap.TryGetValue(chunk.SourceFile ?? string.Empty, out var fullFileReconstructable);
                chunk.IsFullFileReconstructable = fullFileReconstructable;
            }
        }

        private static string FormatCorpusManifestForToolResult(List<ProcessedCorpusSourceFile> corpusManifest)
        {
            var manifest = string.Join("\n\n", corpusManifest.Select(file =>
                $"FileName: {file.Name}\n" +
                $"Description: {file.Description}\n" +
                $"IsFullyIndexed: {file.IsFullyIndexed}"));

            return manifest + SystemPromptBuilder.ManifestAbsentFileReminderSentinel;
        }

        private static bool IsGetFullFileToolCallWasteful(ToolExecutionContext context, List<MessageParam> messages)
        {
            var fileName = context.Input.TryGetValue("fileName", out var value) ? value.ToString() : string.Empty;
            string toolUsedId = "";

            lock (context.ChunksByToolUseIdLock)
            {
                for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
                {
                    if (!messages[messageIndex].Content.TryPickContentBlockParams(out var blocks))
                    {
                        continue;
                    }

                    foreach (var block in blocks)
                    {
                        if (block.TryPickToolUse(out var toolUsed) && toolUsed.Name == AgenticTool.get_all_indexed_files.ToString())
                        {
                            toolUsedId = toolUsed.ID;
                        }

                        if (block.TryPickToolResult(out var toolResult) && toolResult.ToolUseID == toolUsedId)
                        {
                            if (toolResult != null && toolResult.Content != null && toolResult.Content.TryPickString(out var contentText))
                            {
                                var entries = contentText
                                    .Split(new[] { "FileName:" }, StringSplitOptions.RemoveEmptyEntries)
                                    .Select(block => new
                                    {
                                        FileName = Regex.Match(block, @"(\S+\.cs)").Groups[1].Value,
                                        IsFullyIndexed = Regex.Match(block, @"IsFullyIndexed:\s*(\S+)").Groups[1].Value
                                    }).ToList();

                                var searchedFile = entries.FirstOrDefault(x => x.FileName == fileName);
                                var parsedIsFullyIndexed = bool.TryParse(searchedFile?.IsFullyIndexed, out var isFullyIndexed);

                                if (parsedIsFullyIndexed)
                                {
                                    return !isFullyIndexed;
                                }

                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        private static bool IsFullFileAlreadyReconstructedFromChunks(ToolExecutionContext context)
        {
            var fileName = context.Input.TryGetValue("fileName", out var value) ? value.ToString() : string.Empty;
            lock (context.ChunksByToolUseIdLock)
            {
                var retrievedChunks = context.ChunksByToolUseId.SelectMany(x => x.Value).ToList();
                return retrievedChunks.Any(x => x.SourceFile == fileName && x.IsFullFileReconstructable == true);
            }
        }
    }
}
