using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using TradingApp.Infrastructure.Enums;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.Retrieval;

namespace TradingApp.Infrastructure.Services.Retrieval
{
    public class QueryRoutingService : IQueryRoutingService
    {
        private readonly ILogger<QueryRoutingService> _logger;
        private readonly IAnthropicApiService _anthropicApiService;
        //TODO: move this and the other LLM query to some enviornment variables so that they can be changed without re-deployment.
        private const string _llmQueryRouteSystemInstruction = "Classify the following question about a codebase as either BROAD " +
           "(asking for an overview, end-to-end explanation, or how something works as a whole) or NARROW (asking about one specific fact, value, or line)." +
           " Respond with exactly one word: BROAD or NARROW.";

        public QueryRoutingService
        (
            ILogger<QueryRoutingService> logger,
            IAnthropicApiService anthropicApiService
        )
        {
            _logger = logger;
            _anthropicApiService = anthropicApiService;
        }

        public async Task<LlmQueryClassification> LlmQueryRouteAsync(string userMessage)
        {
            var parameters = new MessageCreateParams
            {
                Model = "claude-haiku-4-5",
                MaxTokens = 10,
                System = _llmQueryRouteSystemInstruction,
                Messages = [new() { Role = Role.User, Content = userMessage }]
            };

            try
            {
                var response = await _anthropicApiService.DispatchPromptAsync(parameters, userMessage);

                if (!string.IsNullOrWhiteSpace(response))
                {
                    if (response.ToUpper() == LlmQueryClassification.NARROW.ToString()) return LlmQueryClassification.NARROW;
                    if (response.ToUpper() == LlmQueryClassification.BROAD.ToString()) return LlmQueryClassification.BROAD;
                }

                return LlmQueryClassification.INCONCLUSIVE;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QueryClassificationUnexpectedFailure | Question: {UserMessage}", userMessage);
                return LlmQueryClassification.INCONCLUSIVE;
            }
        }
    }
}
