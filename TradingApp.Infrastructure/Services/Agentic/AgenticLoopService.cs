using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using TradingApp.Infrastructure.Enums;
using TradingApp.Infrastructure.Helpers.Agentic;
using TradingApp.Infrastructure.Helpers.Anthropic;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.Agentic;
using TradingApp.Infrastructure.Interfaces.Anthropic;
using TradingApp.Infrastructure.Models.Agentic;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Services.Agentic
{
    public class AgenticLoopService : IAgenticLoopService
    {
        private readonly ILogger<AgenticLoopService> _logger;
        private readonly IFileDebugLogger _fileDebugLogger;
        private readonly IAnthropicApiService _anthropicApiService;
        private readonly IToolDispatcherService _toolDispatcherService;

        private const int MaxResponseTokens = 4096;

        public AgenticLoopService
        (
            ILogger<AgenticLoopService> logger,
            IFileDebugLogger fileDebugLogger,
            IAnthropicApiService anthropicApiService,
            IToolDispatcherService toolDispatcherService
        )
        {
            _logger = logger;
            _fileDebugLogger = fileDebugLogger;
            _anthropicApiService = anthropicApiService;
            _toolDispatcherService = toolDispatcherService;
        }

        public async IAsyncEnumerable<string> RunAgenticLoopAsync(AgenticLoopRequest request)
        {
            var userMessage = request.UserMessage;
            var conversationMessagesHistory = request.ConversationMessagesHistory;
            var compactedSummary = request.CompactedSummary;
            var usageHandler = request.UsageHandler;
            var maxTokensHandler = request.MaxTokensHandler;
            var responseTruncatedHandler = request.ResponseTruncatedHandler;
            var unexpectedStopReasonHandler = request.UnexpectedStopReasonHandler;

            _logger.LogInformation("RunAgenticLoopAsyncStarted | UserMessage:{UserMessage}", userMessage);
            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", "Loop started", userMessage);

            List<MessageParam> messages = conversationMessagesHistory.Count == 0
                ? new List<MessageParam> { new MessageParam { Role = Role.User, Content = userMessage } }
                : new List<MessageParam>(conversationMessagesHistory);

            var seenChunkKeysLock = new object();
            var seenChunkKeys = new HashSet<string>();
            var chunksByToolUseIdLock = new object();
            var chunksByToolUseId = new Dictionary<string, List<RetrievedChunk>>();
            var anthropicCallCounter = 0;
            var maxTokensIterationCounter = 0;
            var toolCallCounters = new ToolCallCounters();

            while (true)
            {
                var apiCallNumber = ++anthropicCallCounter;

                var parameters = new MessageCreateParams
                {
                    Model = "claude-sonnet-5",
                    MaxTokens = MaxResponseTokens,
                    System = SystemPromptBuilder.BuildAgenticChatSystemPrompt(MaxResponseTokens, compactedSummary),
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

                StopReason stopReason = StopReason.EndTurn;
                MessageDeltaUsage? tokenUsage = null;

                await foreach (var streamedTextChunk in _anthropicApiService.EstablishAgenticLoopStreamAsync
                (
                    userMessage,
                    parameters,
                    (stopReasonEmitted) =>
                    {
                        stopReason = stopReasonEmitted;
                        return Task.CompletedTask;
                    },
                    (usage) =>
                    {
                        tokenUsage = usage;
                        return Task.CompletedTask;
                    }
                ))
                {
                    await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"[ApiCall #{apiCallNumber}] Streamed chunk",
                        streamedTextChunk);
                    yield return streamedTextChunk;
                }

                _logger.LogInformation("RunAgenticLoopStreamAsyncEnded | StopReason: {StopReason}", stopReason);
                await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"[ApiCall #{apiCallNumber}] Stream ended — StopReason: {stopReason}", "");

                if (stopReason == StopReason.MaxTokens || stopReason == StopReason.ToolUse)
                {
                    var response = await _anthropicApiService.DispatchPromptWithFullResponseAsync(parameters, userMessage);

                    if (response == null)
                    {
                        _logger.LogWarning("RunAgenticLoopAsyncFailedRetrievingResponse| UserMessage:{UserMessage}", userMessage);
                        await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                            "Loop ended — null response", "DispatchPromptWithFullResponseAsync returned null");

                        if (tokenUsage is not null)
                        {
                            await usageHandler(tokenUsage);
                        }
                        else
                        {
                            _logger.LogWarning("RunAgenticLoopAsyncMissingTokenUsage | UserMessage:{UserMessage}", userMessage);
                        }

                        yield break;
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

                                messages.Add(new MessageParam
                                {
                                    Role = Role.Assistant,
                                    Content = new List<ContentBlockParam> { new TextBlockParam(maxTokensTextBlock.Text) }
                                });

                                messages.Add(new MessageParam
                                {
                                    Role = Role.User,
                                    Content = SystemPromptBuilder.MaxTokensContinuationPrompt
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
                            }
                            else
                            {
                                await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                                    $"Loop ended — StopReason: {response.StopReason} | returning without response",
                                    "no usable textBlock detected - returning empty response");
                            }

                            await usageHandler(ToMessageDeltaUsage(response.Usage));
                            await maxTokensHandler(parameters.MaxTokens * 2);
                            await responseTruncatedHandler(response.StopReason.ToString());

                            yield break;
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

                                await usageHandler(ToMessageDeltaUsage(response.Usage));
                                await maxTokensHandler(parameters.MaxTokens);

                                if (response.StopReason != null && AnthropicStopReasonEvaluator.IsNotCoveredStopReason(response.StopReason))
                                {
                                    await unexpectedStopReasonHandler(response.StopReason);
                                }

                                yield break;
                            }
                        }

                        _logger.LogWarning("RunAgenticLoopAsyncEndedTextBlockNotFound | StopReason:{StopReason}", response.StopReason);

                        await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                            $"Loop ended — {response.StopReason}, no text block found", "");

                        await usageHandler(ToMessageDeltaUsage(response.Usage));
                        await maxTokensHandler(parameters.MaxTokens);

                        if (response.StopReason != null && AnthropicStopReasonEvaluator.IsNotCoveredStopReason(response.StopReason))
                        {
                            await unexpectedStopReasonHandler(response.StopReason);
                        }

                        yield return "";
                        yield break;
                    }

                    var assistantContent = new List<ContentBlockParam>();
                    var toolResultsContent = new List<ContentBlockParam>();
                    var fetchedFullFileNames = new List<string>();

                    ToolDispatchContext toolDispatchContext = new ToolDispatchContext
                    {
                        Request = request,
                        Response = response,
                        AssistantContentLock = new object(),
                        AssistantContent = assistantContent,
                        ToolResultsContentLock = new object(),
                        ToolResultsContent = toolResultsContent,
                        Counters = toolCallCounters,
                        ApiCallNumber = apiCallNumber,
                        SeenChunkKeysLock = seenChunkKeysLock,
                        SeenChunkKeys = seenChunkKeys,
                        ChunksByToolUseIdLock = chunksByToolUseIdLock,
                        ChunksByToolUseId = chunksByToolUseId,
                        Messages = messages,
                        FetchedFullFileNamesLock = new object(),
                        FetchedFullFileNames = fetchedFullFileNames
                    };

                    await DispatchToolUseBlocksAsync(toolDispatchContext);

                    await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"ApiCall #{apiCallNumber} ends", "");

                    toolResultsContent = await ApplySameRoundChunkDedupAsync(
                        fetchedFullFileNames, toolResultsContent, chunksByToolUseIdLock, chunksByToolUseId);

                    messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });
                    messages.Add(new MessageParam { Role = Role.User, Content = toolResultsContent });
                }
                else
                {
                    if (tokenUsage is not null)
                    {
                        await usageHandler(tokenUsage);
                    }
                    else
                    {
                        _logger.LogWarning("RunAgenticLoopAsyncMissingTokenUsage | UserMessage:{UserMessage}", userMessage);
                    }

                    await maxTokensHandler(parameters.MaxTokens);
                    yield break;
                }
            }
        }

        private async Task<List<ContentBlockParam>> ApplySameRoundChunkDedupAsync
        (
            List<string> fetchedFullFileNames,
            List<ContentBlockParam> toolResultsContent,
            object chunksByToolUseIdLock,
            Dictionary<string, List<RetrievedChunk>> chunksByToolUseId
        )
        {
            foreach (var fetchedFileName in fetchedFullFileNames)
            {
                var toolResultsWrapper = new List<MessageParam> { new MessageParam { Role = Role.User, Content = toolResultsContent } };

                var sameRoundRemovalsLog = ChunkResultDeduplication.RemoveNowRedundantChunksForFile(
                    fetchedFileName, toolResultsWrapper, chunksByToolUseIdLock, chunksByToolUseId);

                if (toolResultsWrapper[0].Content.TryPickContentBlockParams(out var prunedToolResultsContent))
                {
                    toolResultsContent = prunedToolResultsContent.ToList();
                }

                _logger.LogInformation("RemoveNowRedundantChunksForFile (same-round) | FileName:{FileName} | Removals:{RemovalCount}",
                    fetchedFileName, sameRoundRemovalsLog.Count);

                await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                    $"Same-round chunk dedup after get_full_file: {fetchedFileName}",
                    sameRoundRemovalsLog.Count > 0 ? string.Join("\n", sameRoundRemovalsLog) : "No same-round chunks found for this file - nothing to dedup.");
            }

            return toolResultsContent;
        }

        private async Task DispatchToolUseBlocksAsync(ToolDispatchContext context)
        {
            var request = context.Request;
            var response = context.Response;
            var assistantContentLock = context.AssistantContentLock;
            var assistantContent = context.AssistantContent;
            var toolResultsContentLock = context.ToolResultsContentLock;
            var toolResultsContent = context.ToolResultsContent;
            var chunksByToolUseIdLock = context.ChunksByToolUseIdLock;
            var chunksByToolUseId = context.ChunksByToolUseId;
            var messages = context.Messages;
            var apiCallNumber = context.ApiCallNumber;
            var counters = context.Counters;
            var fetchedFullFileNamesLock = context.FetchedFullFileNamesLock;
            var fetchedFullFileNames = context.FetchedFullFileNames;

            var tasks = response.Content.Select(async block =>
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

                    await request.ToolCalledHandler(toolUseBlock.Name);

                    var isKnownTool = Enum.TryParse<AgenticTool>(toolUseBlock.Name, out var toolName);

                    var toolDispatchResponse = await _toolDispatcherService.DispatchToolCallAsync(new ToolDispatchRequest
                    {
                        Context = context,
                        ToolUseBlock = toolUseBlock,
                        IsKnownTool = isKnownTool,
                        ToolName = toolName
                    });

                    var toolTextResult = toolDispatchResponse.Text;
                    var toolResultIsError = toolDispatchResponse.IsError;
                    var currentToolCallNumber = toolDispatchResponse.CurrentToolCallNumber;

                    if (toolName == AgenticTool.search_knowledge_base
                           && !string.IsNullOrEmpty(toolTextResult)
                           && !toolTextResult.StartsWith(SystemPromptBuilder.SearchCapReachedSentinel))
                    {
                        Interlocked.Decrement(ref counters.SearchKnowledgeBaseToolCounter);
                    }
                    else if (toolName == AgenticTool.get_full_file && !toolTextResult.StartsWith(SystemPromptBuilder.NoFullFileContentAvailableSentinel))
                    {
                        try
                        {
                            var fileName = toolUseBlock.Input["fileName"].GetString() ?? string.Empty;
                            var removalsLog = ChunkResultDeduplication.RemoveNowRedundantChunksForFile(
                                fileName, messages, chunksByToolUseIdLock, chunksByToolUseId);

                            _logger.LogInformation("RemoveNowRedundantChunksForFile " +
                                "| FileName:{FileName} | Removals:{RemovalCount}", fileName, removalsLog.Count);

                            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                                $"Chunk dedup after get_full_file: {fileName}",
                                removalsLog.Count > 0 ? string.Join("\n", removalsLog) : "No prior chunks found for this file - nothing to dedup.");

                           
                            lock (fetchedFullFileNamesLock)
                            {
                                fetchedFullFileNames.Add(fileName);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "get_full_file tool call succeeded | UnableToRemoveRedundantChunksForFile ");

                            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"[ApiCall #{apiCallNumber}]" +
                                $" Result from tool #{currentToolCallNumber}: {toolName}",
                                "Failed removing redundant chunks for the file.");
                        }
                    }

                    lock (toolResultsContentLock)
                    {
                        toolResultsContent.Add(new ToolResultBlockParam(toolUseBlock.ID)
                        {
                            Content = toolTextResult,
                            IsError = toolResultIsError
                        });
                    }
                }
            }).ToList();

            await Task.WhenAll(tasks);
        }

        private static MessageDeltaUsage ToMessageDeltaUsage(Usage usage)
        {
            return new MessageDeltaUsage
            {
                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                CacheCreationInputTokens = usage.CacheCreationInputTokens,
                CacheReadInputTokens = usage.CacheReadInputTokens,
                OutputTokensDetails = usage.OutputTokensDetails,
                ServerToolUse = usage.ServerToolUse
            };
        }
    }
}
