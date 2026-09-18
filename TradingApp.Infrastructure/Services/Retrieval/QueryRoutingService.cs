using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using TradingApp.Infrastructure.Enums;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.Retrieval;

namespace TradingApp.Infrastructure.Services.Retrieval
{
    public class QueryRoutingService : IQueryRoutingService
    {
        private readonly ILogger<QueryRoutingService> _logger;
        private readonly IAnthropicApiService _anthropicApiService;

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
                System = SystemPromptBuilder.QueryRouteSystemInstruction,
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
