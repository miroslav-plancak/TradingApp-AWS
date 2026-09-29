using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TradingApp.Infrastructure.Enums;
using TradingApp.Infrastructure.Helpers.Agentic;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.Agentic;
using TradingApp.Infrastructure.Interfaces.Retrieval;
using TradingApp.Infrastructure.Models.Agentic;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Services.Agentic
{
    public class AgenticLoopService : IAgenticLoopService
    {
        private readonly ILogger<AgenticLoopService> _logger;
        private readonly IFileDebugLogger _fileDebugLogger;
        private readonly IAnthropicApiService _anthropicApiService;
        private readonly IQueryDecompositionService _queryDecompositionService;
        private readonly IContextRetrievalService _chunkRetrievalService;

        private const int MaxResponseTokens = 4096;

        public AgenticLoopService
        (
            ILogger<AgenticLoopService> logger,
            IFileDebugLogger fileDebugLogger,
            IAnthropicApiService anthropicApiService,
            IQueryDecompositionService queryDecompositionService,
            IContextRetrievalService chunkRetrievalService
        )
        {
            _logger = logger;
            _fileDebugLogger = fileDebugLogger;
            _anthropicApiService = anthropicApiService;
            _queryDecompositionService = queryDecompositionService;
            _chunkRetrievalService = chunkRetrievalService;
        }

        public async Task<string?> RunAgenticLoopAsync
        (
            string userMessage,
            IReadOnlyList<MessageParam> conversationMessagesHistory,
            string? compactedSummary
        )
        {
            _logger.LogInformation("RunAgenticLoopAsyncStarted | UserMessage:{UserMessage}", userMessage);
            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", "Loop started", userMessage);

            List<MessageParam> messages = conversationMessagesHistory.Count == 0
                ? new List<MessageParam> { new MessageParam { Role = Role.User, Content = userMessage } }
                : new List<MessageParam>(conversationMessagesHistory);

            var seenChunkKeys = new HashSet<string>();
            var searchKnowledgeBaseToolCounter = 0;
            var chunksByToolUseId = new Dictionary<string, List<RetrievedChunk>>(); 

            while (true)
            {
                var parameters = new MessageCreateParams
                {
                    Model = "claude-sonnet-5",
                    MaxTokens = MaxResponseTokens,
                    System = SystemPromptBuilder.BuildAgenticChatSystemPrompt(MaxResponseTokens, compactedSummary),
                    Tools = new List<ToolUnion>
                    {
                        AgenticToolDefinitions.DecomposeQuery,
                        AgenticToolDefinitions.SearchKnowledgeBase,
                        AgenticToolDefinitions.GetFullFile
                    },
                    Messages = messages
                };

                var response = await _anthropicApiService.DispatchPromptWithFullResponseAsync(parameters, userMessage);

                if (response == null)
                {
                    _logger.LogWarning("RunAgenticLoopAsyncFailedRetrievingResponse| UserMessage:{UserMessage}", userMessage);
                    await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", 
                        "Loop ended — null response", "DispatchPromptWithFullResponseAsync returned null");
                    return null;
                }

                if(response.StopReason != StopReason.ToolUse)
                {
                    foreach( var block in response.Content)
                    {
                        if(block.TryPickText(out var textBlock))
                        {
                            _logger.LogInformation("RunAgenticLoopAsyncEnded | StopReason:{StopReason}", response.StopReason);
                            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"Loop ended — {response.StopReason}", textBlock.Text);
                            return textBlock.Text;
                        }
                    }

                    _logger.LogWarning("RunAgenticLoopAsyncEndedTextBlockNotFound | StopReason:{StopReason}", response.StopReason);
                    await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", 
                        $"Loop ended — {response.StopReason}, no text block found", "");
                    return null;
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

                        var isKnownTool = Enum.TryParse<AgenticTool>(toolUseBlock.Name, out var tool);
                        string toolTextResult;

                        if (!isKnownTool)
                        {
                            toolTextResult = $"Unknown tool: {toolUseBlock.Name}";
                            _logger.LogWarning("RunAgenticLoopAsync | UnknownToolCalled:{ToolName} | Input: {Input}", toolUseBlock.Name, toolUseBlock.Input);
                            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"Unknown tool called: {toolUseBlock.Name}", toolUseBlock.Input);
                        }
                        else if (tool == AgenticTool.search_knowledge_base && searchKnowledgeBaseToolCounter >= 3)
                        {
                            toolTextResult = SystemPromptBuilder.SearchCapReachedMessage;

                            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace",
                                "Search capped — consecutive empty limit reached", toolUseBlock.Input);
                        }
                        else
                        {
                            var toolExecutionContext = new ToolExecutionContext 
                            {
                               ToolUseId = toolUseBlock.ID, 
                               ToolName = tool, 
                               Input =  toolUseBlock.Input, 
                               SeenChunkKeys =  seenChunkKeys, 
                               ChunksByToolUseId = chunksByToolUseId 
                            };

                            toolTextResult = await ExecuteToolAsync(toolExecutionContext);

                            if(tool == AgenticTool.search_knowledge_base)
                            {
                                searchKnowledgeBaseToolCounter = string.IsNullOrEmpty(toolTextResult) ? searchKnowledgeBaseToolCounter + 1 : 0;
                            }
                            else if (tool == AgenticTool.get_full_file && !toolTextResult.StartsWith("No full file content available"))
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

                messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });
                messages.Add(new MessageParam { Role = Role.User, Content = toolResultsContent });
            }
        }

        private async Task<string> ExecuteToolAsync(ToolExecutionContext context)
        {
            _logger.LogInformation("RunAgenticLoopAsync | ToolCalled:{ToolName} | Input: {Input}", context.ToolName, context.Input);
            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"Tool called: {context.ToolName}", context.Input);

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
                        result = await HandleGetFullFileAsync(context.Input);
                        break;
                    }
                default:
                    result = $"Unknown tool: {context.ToolName}";
                    break;
            }

            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"Result from: {context.ToolName}", result);

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
            var retrievedChunks = await _chunkRetrievalService.RetrieveRelevantChunksAsync(query);
            var newChunks = retrievedChunks.Where(chunk => context.SeenChunkKeys.Add(chunk.Key ?? string.Empty)).ToList(); 
            var uShapeSortedChunks = ChunkReordering.ReorderChunksToUShape(newChunks);

            context.ChunksByToolUseId[context.ToolUseId] = uShapeSortedChunks;

            return ChunkResultDeduplication.FormatChunksForToolResult(uShapeSortedChunks);
        }

        private async Task<string> HandleGetFullFileAsync(IReadOnlyDictionary<string, JsonElement> input)
        {
            var fileName = input["fileName"].GetString() ?? string.Empty;
            var fullFileContent = await _chunkRetrievalService.GetFullFileContentAsync(fileName);

            return string.IsNullOrEmpty(fullFileContent)
                ? $"No full file content available for {fileName}."
                : $"FileName: {fileName} \n\n{fullFileContent}";
        }
    }
}
