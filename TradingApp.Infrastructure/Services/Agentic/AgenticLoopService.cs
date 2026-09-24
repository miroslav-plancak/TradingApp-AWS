using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TradingApp.Infrastructure.Helpers.Agentic;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.Agentic;
using TradingApp.Infrastructure.Interfaces.Retrieval;

namespace TradingApp.Infrastructure.Services.Agentic
{
    public class AgenticLoopService : IAgenticLoopService
    {
        private readonly ILogger<AgenticLoopService> _logger;
        private readonly IFileDebugLogger _fileDebugLogger;
        private readonly IAnthropicApiService _anthropicApiService;
        private readonly IQueryDecompositionService _queryDecompositionService;
        private readonly IChunkRetrievalService _chunkRetrievalService;

        private const int MaxResponseTokens = 4096;

        public AgenticLoopService
        (
            ILogger<AgenticLoopService> logger,
            IFileDebugLogger fileDebugLogger,
            IAnthropicApiService anthropicApiService,
            IQueryDecompositionService queryDecompositionService,
            IChunkRetrievalService chunkRetrievalService
        )
        {
            _logger = logger;
            _fileDebugLogger = fileDebugLogger;
            _anthropicApiService = anthropicApiService;
            _queryDecompositionService = queryDecompositionService;
            _chunkRetrievalService = chunkRetrievalService;
        }

        public async Task<string?> RunAgenticLoopAsync(string userMessage)
        {
            _logger.LogInformation("RunAgenticLoopAsyncStarted | UserMessage:{UserMessage}", userMessage);
            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", "Loop started", userMessage);

            var messages = new List<MessageParam>
            {
                new MessageParam {Role = Role.User, Content = userMessage}
            };

            var seenChunkKeys = new HashSet<string>();
            var searchKnowledgeBaseToolCounter = 0;

            while (true)
            {
                var parameters = new MessageCreateParams
                {
                    Model = "claude-sonnet-5",
                    MaxTokens = MaxResponseTokens,
                    System = SystemPromptBuilder.BuildAgenticChatSystemPrompt(MaxResponseTokens),
                    Tools = new List<ToolUnion>
                    {
                        AgenticToolDefinitions.SearchKnowledgeBase,
                        AgenticToolDefinitions.DecomposeQuery
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

                        string toolTextResult;

                        if (toolUseBlock.Name == "search_knowledge_base" && searchKnowledgeBaseToolCounter >= 3)
                        {
                            toolTextResult = "No new results found in the last 3 attempts on this line of inquiry - " +
                                "stop retrying this specific angle and either move on or answer with what you already have.";

                            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", 
                                "Search capped — consecutive empty limit reached", toolUseBlock.Input);
                        }
                        else
                        {
                            toolTextResult = await ExecuteToolAsync(toolUseBlock.Name, toolUseBlock.Input, seenChunkKeys);

                            if(toolUseBlock.Name == "search_knowledge_base")
                            {
                                searchKnowledgeBaseToolCounter = string.IsNullOrEmpty(toolTextResult) ? searchKnowledgeBaseToolCounter + 1 : 0;
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

        private async Task<string> ExecuteToolAsync(string toolName, IReadOnlyDictionary<string, JsonElement> input, HashSet<string> seenChunkKeys)
        {
            _logger.LogInformation("RunAgenticLoopAsync | ToolCalled:{ToolName} | Input: {Input}", toolName, input);
            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"Tool called: {toolName}", input);

            string result;

            switch (toolName)
            {
                case "decompose_query":
                    {
                        var question = input["question"].GetString() ?? string.Empty;
                        var subQueries = await _queryDecompositionService.DecomposeQueryAsync(question);
                        result = string.Join("\n", subQueries.Select((subQuery, i) => $"{i + 1}.{subQuery}"));
                        break;
                    }
                case "search_knowledge_base":
                    {
                        var query = input["query"].GetString() ?? string.Empty;
                        var retrievedChunks = await _chunkRetrievalService.RetrieveRelevantChunksAsync(query);
                        var newChunks = retrievedChunks.Where(chunk => seenChunkKeys.Add(chunk.Key ?? string.Empty)).ToList();
                        var uShapeSortedChunks = ChunkReordering.ReorderChunksToUShape(newChunks);
                        result = string.Join("\n", uShapeSortedChunks.Select((chunk, i) =>
                            $"#{i + 1}\n Key: {chunk.Key} " +
                            $"\n FileName: {chunk.SourceFile}" +
                            $"\n RelevanceScore: {chunk.RelevanceScore}" +
                            $"\n\n{chunk.Content} "
                        ));
                        break;
                    }

                default:
                    result = $"Unknown tool: {toolName}";
                    break;
            }

            await _fileDebugLogger.LogSectionAsync("agentic-loop-trace", $"Result from: {toolName}", result);

            return result;
        }
    }
}
