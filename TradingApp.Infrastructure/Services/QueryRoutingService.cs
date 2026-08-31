using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using TradingApp.Infrastructure.Enums;
using TradingApp.Infrastructure.Interfaces;

namespace TradingApp.Infrastructure.Services
{

    public class QueryRoutingService : IQueryRoutingService
    {
        private readonly ILogger<QueryRoutingService> _logger;
        private readonly AnthropicClient _anthropicClient;

        private const string _llmQueryRouteSystemInstruction = "Classify the following question about a codebase as either BROAD " +
           "(asking for an overview, end-to-end explanation, or how something works as a whole) or NARROW (asking about one specific fact, value, or line)." +
           " Respond with exactly one word: BROAD or NARROW.";
        public QueryRoutingService(ILogger<QueryRoutingService> logger, AnthropicClient anthropicClient)
        {
            _logger = logger;
            _anthropicClient = anthropicClient;
        }

        public async Task<LlmQueryClassification> LlmQueryRouteAsync(string userQuestion)
        {
            var parameters = new MessageCreateParams
            {
                Model = "claude-haiku-4-5",
                MaxTokens = 10,
                System = _llmQueryRouteSystemInstruction,
                Messages = [new() { Role = Role.User, Content = userQuestion }]
            };

            try
            {
                var messageResponse = await _anthropicClient.Messages.Create(parameters);
                var firstBlock = messageResponse.Content.FirstOrDefault();

                if (firstBlock is not null && firstBlock.TryPickText(out var textblock))
                {
                    var classificationResponseText = textblock.Text.Trim();

                    if (classificationResponseText.ToUpper() == LlmQueryClassification.NARROW.ToString()) return LlmQueryClassification.NARROW;
                    if (classificationResponseText.ToUpper() == LlmQueryClassification.BROAD.ToString()) return LlmQueryClassification.BROAD;
                }

                return LlmQueryClassification.INCONCLUSIVE;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while dispatching query classification for question: {UserQuestion}", userQuestion);
                return LlmQueryClassification.INCONCLUSIVE;
            }
        }
    }
}
