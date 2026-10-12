using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using System.Text;
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
            var seenChunkKeysLock = new object();
            var seenChunkKeys = new HashSet<string>();
            var searchKnowledgeBaseToolCounter = 0;
            var chunksByToolUseIdLock = new object();
            var chunksByToolUseId = new Dictionary<string, List<RetrievedChunk>>();
            var anthropicCallCounter = 0;
            var maxTokensIterationCounter = 0;
            var maxTokensResponseAccumulator = new StringBuilder();
          
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

                if (response.StopReason == StopReason.MaxTokens)
                {
                    TextBlock? maxTokensTextBlock = null;
                    var maxTokensToolUseBlock = false;

                    foreach (var block in response.Content)
                    {
                        if (maxTokensTextBlock == null && block.TryPickText(out var foundTextBlock))
                        {
                            maxTokensTextBlock = foundTextBlock;
                        }
                        else if (block.TryPickToolUse(out _))
                        {
                            maxTokensToolUseBlock = true;
                        }
                    }

                    // textBlock and toolUseBlock in the same response is narration before toolUse - we retry. (isContinuable = false)
                    // textBlock exists and toolUseBlock is absent in the response - we continue. (isContinuable = true)
                    var isContinuable = maxTokensTextBlock != null && !maxTokensToolUseBlock;

                    ++maxTokensIterationCounter;

                    if (maxTokensIterationCounter != 2)
                    {
                        if (isContinuable)
                        {
                            _logger.LogInformation("RunAgenticLoopAsyncStoppedTextBlockExists | continuing | StopReason:{StopReason} ", response.StopReason);

                            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                                $"Loop stopped —  StopReason:{response.StopReason} | continuing", maxTokensTextBlock!.Text);

                            maxTokensResponseAccumulator.Append(maxTokensTextBlock.Text);

                            messages.Add(new MessageParam
                            {
                                Role = Role.User,
                                Content = SystemPromptBuilder.BuildMaxTokensContinuationMessage(maxTokensResponseAccumulator.ToString())
                            });
                        }
                        else
                        {
                            // toolUseBlock or thinkingBlock cut off mid way - retry with same request, no messages saved.
                            _logger.LogInformation("RunAgenticLoopAsyncStoppedNoUsableText | retrying | StopReason:{StopReason}",
                                response.StopReason);

                            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", 
                                $"Loop stopped —  StopReason: {response.StopReason} | retrying",
                                "no usable textBlock detected - retrying unchanged");
                        }

                        continue;
                    }
                    else
                    {
                        _logger.LogInformation("RunAgenticLoopAsyncMaxTokenBudgetSpent | returning | StopReason:{StopReason}", response.StopReason);

                        if (isContinuable)
                        {
                            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"Loop ended — StopReason: {response.StopReason} | returning response",
                                maxTokensTextBlock!.Text);

                            maxTokensResponseAccumulator.Append(maxTokensTextBlock.Text);
                        }
                        else
                        {
                            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                                $"Loop ended — StopReason: {response.StopReason} | returning without response", 
                                "no usable textBlock detected - returning empty response");
                        }

                        agenticLoopResponse.MessageResponse = maxTokensResponseAccumulator.ToString();
                        agenticLoopResponse.TokenUsage = response.Usage;
                        agenticLoopResponse.MaxTokens = parameters.MaxTokens * 2;

                        return agenticLoopResponse;
                    }
                }
                else if (response.StopReason != StopReason.ToolUse)
                {
                    foreach (var block in response.Content)
                    {
                        if (block.TryPickText(out var textBlock))
                        {
                            _logger.LogInformation("RunAgenticLoopAsyncEnded | StopReason: {StopReason}", response.StopReason);
                            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"Loop ended — StopReason: {response.StopReason}",
                                textBlock.Text);

                            maxTokensResponseAccumulator.Append(textBlock.Text);

                            agenticLoopResponse.MessageResponse = maxTokensIterationCounter == 1 ? maxTokensResponseAccumulator.ToString() : textBlock.Text;
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

                var assistantContentLock = new object();
                var assistantContent = new List<ContentBlockParam>();
                var toolResultsContentLock = new object();
                var toolResultsContent = new List<ContentBlockParam>();
                var toolCallNumber = 0;

                var tasks = response.Content.Select( async block => 
                { 
                    if (block.TryPickText(out var textblock))
                    {
                        lock (assistantContentLock)
                        {
                            assistantContent.Add(new TextBlockParam(textblock.Text));
                        }
                    }
                    else if (block.TryPickToolUse(out var toolUseBlock))
                    {
                        lock (assistantContentLock)
                        {
                            assistantContent.Add(new ToolUseBlockParam
                            {
                                ID = toolUseBlock.ID,
                                Name = toolUseBlock.Name,
                                Input = toolUseBlock.Input
                            });
                        }
                        
                        var isKnownTool = Enum.TryParse<AgenticTool>(toolUseBlock.Name, out var toolName);
                        string toolTextResult;
                        
                        try
                        {
                            if (!isKnownTool)
                            {
                                toolTextResult = $"Unknown tool: {toolUseBlock.Name}";
                                _logger.LogWarning("RunAgenticLoopAsync | UnknownToolCalled:{ToolName}", toolUseBlock.Name);
                                await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"[Call #{callNumber}] Unknown tool called: {toolUseBlock.Name}", toolUseBlock.Input);
                            }
                            else if (toolName == AgenticTool.search_knowledge_base && Interlocked.Increment(ref searchKnowledgeBaseToolCounter) > 3)
                            {
                                Interlocked.Decrement(ref searchKnowledgeBaseToolCounter);

                                toolTextResult = SystemPromptBuilder.SearchCapReachedSentinel;

                                await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                                    $"[Call #{callNumber}] Search capped — consecutive empty limit reached", toolUseBlock.Input);
                            }
                            else
                            {
                                var toolExecutionContext = new ToolExecutionContext
                                {
                                    ConversationId = request.ConversationId,
                                    ToolUseId = toolUseBlock.ID,
                                    ToolCallNumber = Interlocked.Increment(ref toolCallNumber),
                                    ToolName = toolName,
                                    Input = toolUseBlock.Input,
                                    SeenChunkKeysLock = seenChunkKeysLock,
                                    SeenChunkKeys = seenChunkKeys,
                                    ChunksByToolUseIdLock = chunksByToolUseIdLock,
                                    ChunksByToolUseId = chunksByToolUseId,
                                    CallNumber = callNumber
                                };

                                bool shouldSkipGetFullFileToolCall = false;
                                bool manifestSaysNotIndexed = false;
                                bool chunksAlreadyReconstructFile = false;

                                if (toolName == AgenticTool.get_full_file)
                                {
                                    manifestSaysNotIndexed = IsGetFullFileToolCallWasteful(toolExecutionContext, messages);
                                    chunksAlreadyReconstructFile = IsFullFileAlreadyReconstructedFromChunks(toolExecutionContext);

                                    shouldSkipGetFullFileToolCall = manifestSaysNotIndexed || chunksAlreadyReconstructFile;
                                }

                                if (shouldSkipGetFullFileToolCall)
                                {
                                    toolTextResult = $"{SystemPromptBuilder.NoFullFileContentAvailableSentinel} {toolUseBlock.Input["fileName"].ToString()}.";

                                    var reason = manifestSaysNotIndexed ? "manifest shows not fully indexed" : "chunks already reconstruct the full file";

                                    await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                                        $"[Call #{callNumber}] get_full_file intercepted — reason:", reason);
                                }
                                else
                                {
                                    toolTextResult = await ExecuteToolAsync(toolExecutionContext);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            toolTextResult = $"{SystemPromptBuilder.ToolTextResultGenerationErrorMessageSentinel} {toolUseBlock.ID}";

                            _logger.LogWarning(ex, "get_full_file tool call failed | UnableToGenerateToolResult " +
                                "| ToolUseID:{ToolUseID}", toolUseBlock.ID);

                            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"[Call #{callNumber}]" +
                                     $" Result from tool #{Volatile.Read(ref toolCallNumber)}: {toolName}", toolTextResult);
                          
                        }

                        if (toolName == AgenticTool.search_knowledge_base
                               && !string.IsNullOrEmpty(toolTextResult)
                               && !toolTextResult.StartsWith(SystemPromptBuilder.SearchCapReachedSentinel))
                        {
                            Interlocked.Decrement(ref searchKnowledgeBaseToolCounter);
                        }
                        else if (toolName == AgenticTool.get_full_file && !toolTextResult.StartsWith(SystemPromptBuilder.NoFullFileContentAvailableSentinel))
                        {
                            try
                            {
                                var fileName = toolUseBlock.Input["fileName"].GetString() ?? string.Empty;
                                var removalsLog = ChunkResultDeduplication.RemoveNowRedundantChunksForFile(fileName, messages, chunksByToolUseIdLock, chunksByToolUseId);

                                _logger.LogInformation("RemoveNowRedundantChunksForFile " +
                                    "| FileName:{FileName} | Removals:{RemovalCount}", fileName, removalsLog.Count);

                                await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                                    $"Chunk dedup after get_full_file: {fileName}",
                                    removalsLog.Count > 0 ? string.Join("\n", removalsLog) : "No prior chunks found for this file - nothing to dedup.");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "get_full_file tool call failed | UnableToRemoveRedundantChunksForFile ");

                                await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"[Call #{callNumber}]" +
                                     $" Result from tool #{Volatile.Read(ref toolCallNumber)}: {toolName}",
                                     "Failed removing redundant chunks for the file.");
                            }
                        }

                        lock (toolResultsContentLock)
                        {
                            toolResultsContent.Add(new ToolResultBlockParam(toolUseBlock.ID)
                            {
                                Content = toolTextResult
                            });
                        }
                    }
                }).ToList();

                await Task.WhenAll(tasks); 

                await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",$"Call #{callNumber} ends", "");

                messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });
                messages.Add(new MessageParam { Role = Role.User, Content = toolResultsContent });
            }
        }

        private async Task<string> ExecuteToolAsync(ToolExecutionContext context)
        {
            _logger.LogInformation("RunAgenticLoopAsync | ToolCalled:{ToolName}", context.ToolName);
            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                $"[Call #{context.CallNumber}] Tool #{context.ToolCallNumber} called: {context.ToolName}", context.Input);

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

            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"[Call #{context.CallNumber}]" +
                $" Result from tool #{context.ToolCallNumber}: {context.ToolName}", result);

            return result;
        }
        
        private async Task<string> HandleDecomposeQueryAsync(IReadOnlyDictionary<string, JsonElement> input)
        {
            string question;

            try 
            {
                 question = input["question"].GetString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,"HandleDecomposeQueryAsyncFailed | Input: {Input}", JsonSerializer.Serialize(input));
                question = "";
            }

            var subQueries = await _queryDecompositionService.DecomposeQueryAsync(question);

            if (subQueries.Count == 0) return SystemPromptBuilder.DecomposeQueryInvalidInputSentinel;

            return string.Join("\n", subQueries.Select((subQuery, i) => $"{i + 1}.{subQuery}"));
        }

        private async Task<string> HandleSearchKnowledgeBaseAsync(ToolExecutionContext context)
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
            
            var retrievedChunks = await _contextRetrievalService.RetrieveRelevantChunksAsync(query);

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

        private async Task<string> HandleGetFullFileAsync(ToolExecutionContext context)
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
          
            var fullFileContent = await _contextRetrievalService.GetFullFileContentAsync(fileName);

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

        private async Task<string> HandleGetDatabaseContextAsync(ToolExecutionContext context)
        {
            string query;

            try
            {
               query = context.Input["query"].GetString() ?? string.Empty;
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, "HandleGetDatabaseContextAsyncFailed | Input: {Input}", JsonSerializer.Serialize(context.Input));
                query = "";
            }

           
            var reusableContextArtifacts = await _conversationReuseService.TryRetrieveReusableConversationArtifactsAsync(
                context.ConversationId, query);

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

            var fullFilesMap = await _fileExpansionService.GetExistingFullFileContentsMapAsync(uShapeSortedChunks.Select(x => x.SourceFile));

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

        public async Task<string> HandleGetAllIndexedFileNames()
        {
            var corpusManifest = await _corpusManifestService.GetEntireCorpusManifestAsync();

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
            lock(context.ChunksByToolUseIdLock)
            {
               var retrievedChunks = context.ChunksByToolUseId.SelectMany(x => x.Value).ToList();
               return retrievedChunks.Any(x => x.SourceFile == fileName && x.IsFullFileReconstructable == true);
            }
        }
    }
}
