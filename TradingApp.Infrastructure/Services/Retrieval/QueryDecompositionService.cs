using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TradingApp.Infrastructure.Helpers.ConversationMemory;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.Retrieval;

namespace TradingApp.Infrastructure.Services.Retrieval
{
    public class QueryDecompositionService : IQueryDecompositionService
    {
        private readonly ILogger<QueryDecompositionService> _logger;
        private readonly IAnthropicApiService _anthropicApiService;
        private readonly IFileDebugLogger _fileDebugLogger;

        public QueryDecompositionService
        (

            ILogger<QueryDecompositionService> logger,
            IFileDebugLogger fileDebugLogger,
            IAnthropicApiService anthropicApiService
        )
        {
            _logger = logger;
            _fileDebugLogger = fileDebugLogger;
            _anthropicApiService = anthropicApiService;
        }

        public async Task<List<string>> DecomposeQueryAsync(string userMessage)
        {

            var parameters = new MessageCreateParams
            {
                Model = "claude-haiku-4-5",
                MaxTokens = 512,
                System = SystemPromptBuilder.QueryDecompositionSystemInstruction,
                Messages = [new() { Role = Role.User, Content = userMessage}]
            };

            try
            {
                var result = await _anthropicApiService.DispatchPromptAsync(parameters, userMessage);
                var extractedJson = LlmJsonExtractor.ExtractJsonArray(result);

                try
                {
                    var decomposedQueries = JsonSerializer.Deserialize<List<string>>(extractedJson);

                    await _fileDebugLogger.LogSectionAsync("0b-query-decomposition", "Original userMessage was decomposed into the following queries",
                        RetrievalResultLogFormatter.FormatQueryDecompositionResponseIntoFileLog(decomposedQueries));

                    if (decomposedQueries == null || decomposedQueries.Count == 0)
                    {
                        _logger.LogWarning( "QueryDecompositionRetrievedEmptyResult | DecomposedQueries: {QueriesCount}", decomposedQueries?.Count);
                        return [userMessage];
                    }

                    return decomposedQueries;
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "QueryDecompositionJsonParseFailure | Response: {ResponseText}", extractedJson);
                }

                return [userMessage];
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QueryDecompositionUnexpectedFailure | Question: {UserMessage}", userMessage);
                return [userMessage];
            }
        }
    }
}
