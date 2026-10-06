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
using TradingApp.Infrastructure.Models.Ingestion;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Services.Agentic
{
    public class AgenticLoopService : IAgenticLoopService
    {
        private readonly ILogger<AgenticLoopService> _logger;
        private readonly IFileDebugLogger _fileDebugLogger;
        private readonly IAnthropicApiService _anthropicApiService;
        private readonly IQueryDecompositionService _queryDecompositionService;
        private readonly IContextRetrievalService _contextRetrievalService;
        private readonly IConversationReuseService _conversationReuseService;
        private readonly IFileExpansionService _fileExpansionService;
        private readonly ICorpusManifestService _corpusManifestService;

        private const int MaxResponseTokens = 4096;

        public AgenticLoopService
        (
            ILogger<AgenticLoopService> logger,
            IFileDebugLogger fileDebugLogger,
            IAnthropicApiService anthropicApiService,
            IQueryDecompositionService queryDecompositionService,
            IContextRetrievalService contextRetrievalService,
            IConversationReuseService conversationResuseService,
            IFileExpansionService fileExpansionService,
            ICorpusManifestService corpusManifestService
        )
        {
            _logger = logger;
            _fileDebugLogger = fileDebugLogger;
            _anthropicApiService = anthropicApiService;
            _queryDecompositionService = queryDecompositionService;
            _contextRetrievalService = contextRetrievalService;
            _conversationReuseService = conversationResuseService;
            _fileExpansionService = fileExpansionService;
            _corpusManifestService = corpusManifestService;
        }
       
        public async Task<AgenticLoopResponse> RunAgenticLoopAsync(AgenticLoopRequest request)
        {
            _logger.LogInformation("RunAgenticLoopAsyncStarted | UserMessage:{UserMessage}", request.UserMessage);
            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", "Loop started", request.UserMessage);

            List<MessageParam> messages = request.ConversationMessagesHistory.Count == 0
                ? new List<MessageParam> { new MessageParam { Role = Role.User, Content = request.UserMessage } }
                : new List<MessageParam>(request.ConversationMessagesHistory);

            AgenticLoopResponse agenticLoopResponse = new();

            var seenChunkKeys = new HashSet<string>();
            var searchKnowledgeBaseToolCounter = 0;
            var chunksByToolUseId = new Dictionary<string, List<RetrievedChunk>>();
            var anthropicCallCounter = 0;

            while (true)
            {
                var callNumber = ++anthropicCallCounter;

                var parameters = new MessageCreateParams
                {
                    Model = "claude-sonnet-5",
                    MaxTokens = MaxResponseTokens,
                    System = SystemPromptBuilder.BuildAgenticChatSystemPrompt(MaxResponseTokens, request.CompactedSummary),
                    Tools = new List<ToolUnion>
                    {
                        AgenticToolDefinitions.DecomposeQuery,
                        AgenticToolDefinitions.SearchKnowledgeBase,
                        AgenticToolDefinitions.GetFullFile,
                        AgenticToolDefinitions.GetDatabaseContext,
                        AgenticToolDefinitions.GetAllIndexedFiles
                    },
                    Messages = messages
                };

                var response = await _anthropicApiService.DispatchPromptWithFullResponseAsync(parameters, request.UserMessage);

                if (response == null)
                {
                    _logger.LogWarning("RunAgenticLoopAsyncFailedRetrievingResponse| UserMessage:{UserMessage}", request.UserMessage);
                    await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                        "Loop ended — null response", "DispatchPromptWithFullResponseAsync returned null");
                    return agenticLoopResponse;
                }

                if (response.StopReason != StopReason.ToolUse)
                {
                    foreach (var block in response.Content)
                    {
                        if (block.TryPickText(out var textBlock))
                        {
                            _logger.LogInformation("RunAgenticLoopAsyncEnded | StopReason:{StopReason}", response.StopReason);
                            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"Loop ended — {response.StopReason}", textBlock.Text);

                            agenticLoopResponse.MessageResponse = textBlock.Text;
                            agenticLoopResponse.TokenUsage = response.Usage;
                            agenticLoopResponse.MaxTokens = parameters.MaxTokens;

                            return agenticLoopResponse;
                          
                        }
                    }

                    _logger.LogWarning("RunAgenticLoopAsyncEndedTextBlockNotFound | StopReason:{StopReason}", response.StopReason);
                    await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                        $"Loop ended — {response.StopReason}, no text block found", "");

                    agenticLoopResponse.MessageResponse = "";
                    agenticLoopResponse.TokenUsage = response.Usage;
                    agenticLoopResponse.MaxTokens = parameters.MaxTokens;

                    return agenticLoopResponse;
                }

                var assistantContent = new List<ContentBlockParam>();
                var toolResultsContent = new List<ContentBlockParam>();

                foreach (var block in response.Content)
                {
                    if (block.TryPickText(out var textblock))
                    {
                        assistantContent.Add(new TextBlockParam(textblock.Text));
                    }
                    else if (block.TryPickToolUse(out var toolUseBlock))
                    {
                        assistantContent.Add(new ToolUseBlockParam
                        {
                            ID = toolUseBlock.ID,
                            Name = toolUseBlock.Name,
                            Input = toolUseBlock.Input
                        });

                        var isKnownTool = Enum.TryParse<AgenticTool>(toolUseBlock.Name, out var toolName);
                        string toolTextResult;

                        if (!isKnownTool)
                        {
                            toolTextResult = $"Unknown tool: {toolUseBlock.Name}";
                            _logger.LogWarning("RunAgenticLoopAsync | UnknownToolCalled:{ToolName} | Input: {Input}", toolUseBlock.Name, toolUseBlock.Input);
                            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"[Call #{callNumber}] Unknown tool called: {toolUseBlock.Name}", toolUseBlock.Input);
                        }
                        else if (toolName == AgenticTool.search_knowledge_base && searchKnowledgeBaseToolCounter >= 3)
                        {
                            toolTextResult = SystemPromptBuilder.SearchCapReachedMessage;

                            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                                $"[Call #{callNumber}] Search capped — consecutive empty limit reached", toolUseBlock.Input);
                        }
                        else
                        {
                            var toolExecutionContext = new ToolExecutionContext
                            {
                                ConversationId = request.ConversationId,
                                ToolUseId = toolUseBlock.ID,
                                ToolName = toolName,
                                Input = toolUseBlock.Input,
                                SeenChunkKeys = seenChunkKeys,
                                ChunksByToolUseId = chunksByToolUseId,
                                CallNumber = callNumber
                            };

                            bool shouldSkipGetFullFileToolCall = false;
                            bool manifestSaysNotIndexed = false;
                            bool chunksAlreadyReconstructFile = false;

                            if (toolName == AgenticTool.get_full_file)
                            {
                                manifestSaysNotIndexed = IsGetFullFileToolCallWasteful(messages, toolUseBlock.Input);
                                chunksAlreadyReconstructFile = IsFullFileAlreadyReconstructedFromChunks(chunksByToolUseId, toolUseBlock.Input);

                                shouldSkipGetFullFileToolCall = manifestSaysNotIndexed || chunksAlreadyReconstructFile;
                            }

                            if (shouldSkipGetFullFileToolCall)
                            {
                                toolTextResult = $"{SystemPromptBuilder.NoFullFileContentAvailable} {toolUseBlock.Input["fileName"].ToString()}.";

                                var reason = manifestSaysNotIndexed ? "manifest shows not fully indexed" : "chunks already reconstruct the full file";

                                await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                                    $"[Call #{callNumber}] get_full_file intercepted — reason:", reason);
                            }
                            else
                            {
                                toolTextResult = await ExecuteToolAsync(toolExecutionContext);
                            }

                            if (toolName == AgenticTool.search_knowledge_base)
                            {
                                searchKnowledgeBaseToolCounter = string.IsNullOrEmpty(toolTextResult) ? searchKnowledgeBaseToolCounter + 1 : 0;
                            }
                            else if (toolName == AgenticTool.get_full_file && !toolTextResult.StartsWith(SystemPromptBuilder.NoFullFileContentAvailable))
                            {
                                var fileName = toolUseBlock.Input["fileName"].GetString() ?? string.Empty;
                                var removalsLog = ChunkResultDeduplication.RemoveNowRedundantChunksForFile(fileName, messages, chunksByToolUseId);

                                _logger.LogInformation("RemoveNowRedundantChunksForFile " +
                                    "| FileName:{FileName} | Removals:{RemovalCount}", fileName, removalsLog.Count);

                                await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                                    $"Chunk dedup after get_full_file: {fileName}",
                                    removalsLog.Count > 0 ? string.Join("\n", removalsLog) : "No prior chunks found for this file - nothing to dedup.");
                            }
                        }

                        toolResultsContent.Add(new ToolResultBlockParam(toolUseBlock.ID)
                        {
                            Content = toolTextResult
                        });
                    }
                }

                await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",$"Call #{callNumber} ends", "");

                messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });
                messages.Add(new MessageParam { Role = Role.User, Content = toolResultsContent });
            }
        }

        private async Task<string> ExecuteToolAsync(ToolExecutionContext context)
        {
            _logger.LogInformation("RunAgenticLoopAsync | ToolCalled:{ToolName} | Input: {Input}", context.ToolName, context.Input);
            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"[Call #{context.CallNumber}] Tool called: {context.ToolName}", context.Input);

            string result;

            switch (context.ToolName)
            {
                case AgenticTool.decompose_query:
                    {
                        result = await HandleDecomposeQueryAsync(context.Input);
                        break;
                    }
                case AgenticTool.search_knowledge_base:
                    {
                        result = await HandleSearchKnowledgeBaseAsync(context);
                        break;
                    }
                case AgenticTool.get_full_file:
                    {
                        result = await HandleGetFullFileAsync(context);
                        break;
                    }
                case AgenticTool.get_database_context:
                    {
                        result = await HandleGetDatabaseContextAsync(context);
                        break;
                    }
                case AgenticTool.get_all_indexed_files:
                    {
                        result = await HandleGetAllIndexedFileNames();
                        break;
                    }
                default:
                    result = $"Unknown tool: {context.ToolName}";
                    break;
            }

            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"[Call #{context.CallNumber}] Result from: {context.ToolName}", result);

            return result;
        }

        private async Task<string> HandleDecomposeQueryAsync(IReadOnlyDictionary<string, JsonElement> input)
        {
            var question = input["question"].GetString() ?? string.Empty;
            var subQueries = await _queryDecompositionService.DecomposeQueryAsync(question);

            return string.Join("\n", subQueries.Select((subQuery, i) => $"{i + 1}.{subQuery}"));
        }

        private async Task<string> HandleSearchKnowledgeBaseAsync(ToolExecutionContext context)
        {
            var query = context.Input["query"].GetString() ?? string.Empty;
            var retrievedChunks = await _contextRetrievalService.RetrieveRelevantChunksAsync(query);
            var newChunks = retrievedChunks.Where(chunk => context.SeenChunkKeys.Add(chunk.Key ?? string.Empty)).ToList();
            var uShapeSortedChunks = ChunkReordering.ReorderChunksToUShape(newChunks);

            ComputeFullFileReconstructableValue(uShapeSortedChunks);

            context.ChunksByToolUseId[context.ToolUseId] = uShapeSortedChunks;

            await _conversationReuseService.TryPersistReusableConversationArtifactsAsync(context.ConversationId, newChunks, []);

            return ChunkResultDeduplication.FormatChunksForToolResult(uShapeSortedChunks);
        }

        private async Task<string> HandleGetFullFileAsync(ToolExecutionContext context)
        {
            var fileName = context.Input["fileName"].GetString() ?? string.Empty;
            var fullFileContent = await _contextRetrievalService.GetFullFileContentAsync(fileName);

            if (string.IsNullOrEmpty(fullFileContent))
            {
                return $"{SystemPromptBuilder.NoFullFileContentAvailable} {fileName}.";
            }
            else
            {
                Dictionary<string, string> fullFileDictionaryEntry = [];
                fullFileDictionaryEntry.Add(fileName, fullFileContent);
                await _conversationReuseService.TryPersistReusableConversationArtifactsAsync(context.ConversationId, [], fullFileDictionaryEntry);

                return $"FileName: {fileName} \n\n{fullFileContent}";
            }
        }

        private async Task<string> HandleGetDatabaseContextAsync(ToolExecutionContext context)
        {
            var query = context.Input["query"].GetString() ?? string.Empty;
            var reusableContextArtifacts = await _conversationReuseService.TryRetrieveReusableConversationArtifactsAsync(
                context.ConversationId, query);

            if (reusableContextArtifacts.ConversationChunks.Count == 0 && reusableContextArtifacts.ConversationFullFiles.Count == 0)
            {
                return "No re-usable context found.";
            }

            var mappedChunks = RetrievalResultMapping.ToRetrievedChunks(reusableContextArtifacts.ConversationChunks);
            var newChunks = mappedChunks.Where(chunk => context.SeenChunkKeys.Add(chunk.Key ?? string.Empty)).ToList();
            var fullFileDedupKeys = reusableContextArtifacts.ConversationFullFiles.Select(x => x.SourceFile).ToHashSet();

            var uShapeSortedChunks = ChunkReordering.ReorderChunksToUShape(
                newChunks.Where(chunk => !fullFileDedupKeys.Contains(chunk.SourceFile ?? string.Empty)).ToList());

            var fullFilesMap = await _fileExpansionService.GetExistingFullFileContentsMapAsync(uShapeSortedChunks.Select(x => x.SourceFile));

            foreach (var chunk in uShapeSortedChunks)
            {
                chunk.IsFullFileIndexed = fullFilesMap.ContainsKey(chunk.SourceFile ?? string.Empty);
            }

            context.ChunksByToolUseId[context.ToolUseId] = uShapeSortedChunks;

            ComputeFullFileReconstructableValue(uShapeSortedChunks);

            var formattedChunks = uShapeSortedChunks.Count > 0
                ? ChunkResultDeduplication.FormatChunksForToolResult(uShapeSortedChunks)
                : string.Empty;

            var fullFileContents = RetrievalResultMapping.ToFullFileContents(reusableContextArtifacts.ConversationFullFiles);

            var formattedFullFiles = fullFileContents.Count > 0
                ? string.Join("\n\n", fullFileContents.Select(x => $"FileName: {x.Key} \n\n{x.Value}"))
                : string.Empty;

            var combinedResult = string.Join("\n\n", new[] { formattedChunks, formattedFullFiles }.Where(s => !string.IsNullOrEmpty(s)));

            return string.IsNullOrEmpty(combinedResult) ? "No re-usable context found." : combinedResult;
        }

        public async Task<string> HandleGetAllIndexedFileNames()
        {
            var corpusManifest = await _corpusManifestService.GetEntireCorpusManifestAsync();

            if (corpusManifest.Count == 0)
            {
                return SystemPromptBuilder.NoSourceFileNamesFound;
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

            return manifest + SystemPromptBuilder.ManifestAbsentFileReminder;
        }

        private static bool IsGetFullFileToolCallWasteful(List<MessageParam> messages, IReadOnlyDictionary<string, JsonElement> input)
        {
            var fileName = input.TryGetValue("fileName", out var value) ? value.ToString() : string.Empty;
            string toolUsedId = "";

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

            return false;
        }

        private static bool IsFullFileAlreadyReconstructedFromChunks
        (
         Dictionary<string, List<RetrievedChunk>> chunksByToolUseId,
         IReadOnlyDictionary<string, JsonElement> input
        )
        {
            var fileName = input.TryGetValue("fileName", out var value) ? value.ToString() : string.Empty;
            var retrievedChunks = chunksByToolUseId.SelectMany(x => x.Value).ToList();
            return retrievedChunks.Any(x => x.SourceFile == fileName && x.IsFullFileReconstructable == true);
        }
    }
}
