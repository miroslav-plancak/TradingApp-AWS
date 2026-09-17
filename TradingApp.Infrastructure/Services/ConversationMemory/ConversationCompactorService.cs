using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using TradingApp.Business.DTOs.ConversationMessage;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.ConversationMemory;

namespace TradingApp.Infrastructure.Services.ConversationMemory
{
    public class ConversationCompactorService : IConversationCompactorService
    {
        private readonly ILogger<ConversationCompactorService> _logger;
        private readonly IAnthropicApiService _anthropicApiService;
        private readonly IConversationService _conversationService;

        private const int Sonnet5MaxContextWindow = 1000000;
        private const double CompactThreshold = 0.85;
        private const double StartingThreshold = 0.35;
        private const int Sonnet5AfterCompactContextWindow = (int)(Sonnet5MaxContextWindow * StartingThreshold);

        public ConversationCompactorService
        (
            ILogger<ConversationCompactorService> logger,
            IAnthropicApiService anthropicApiService,
            IConversationService conversationService
        )
        {
            _logger = logger;
            _anthropicApiService = anthropicApiService;
            _conversationService = conversationService;
        }

        public async Task CompactConversationAsync(Guid conversationId, MessageDeltaUsage usage, long maxTokens)
        {
            if(!IsCompactionThresholdReached(usage, maxTokens)) return;

            var conversationDTO = await _conversationService.GetConversationCompactionStateAsync(conversationId);
            var messagesForCompaction = await _conversationService.GetConversationMessagesForCompactionAsync(
                conversationDTO, Sonnet5AfterCompactContextWindow);

            var parameters = new MessageCreateParams
            {
                Model = "claude-sonnet-5",
                MaxTokens = 7000,
                System = SystemPromptBuilder.BuildCompactionSystemPrompt(conversationDTO.CompactedSummary),
                Messages = ToAnthropicMessageParams(messagesForCompaction)
            };

            try
            {
                var response = await _anthropicApiService.DispatchPromptAsync(parameters);

                if (!string.IsNullOrWhiteSpace(response) && messagesForCompaction.Count != 0)
                {
                    var lastCompactedMessageTimeStamp = messagesForCompaction.Last().CreatedAt;
                    await _conversationService.UpdateCompactedConversationSummaryAsync(conversationId, response, lastCompactedMessageTimeStamp);
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConversationCompactionUnexpectedFailure | ConversationId: {ConversationId}", conversationId);
            }
        }

        private static bool IsCompactionThresholdReached(MessageDeltaUsage usage, long maxTokens)
        {
            var lastTurnTotalInputTokens = usage.InputTokens + usage.CacheCreationInputTokens + usage.CacheReadInputTokens;
            return (lastTurnTotalInputTokens + maxTokens) >= Sonnet5MaxContextWindow * CompactThreshold;
        }

        private static IReadOnlyList<MessageParam> ToAnthropicMessageParams(List<ConversationHistoryMessageDTO> conversationMessages)
        {
            if (conversationMessages.Count == 0) return [];

            return conversationMessages
                .Select(message => new MessageParam()
                {
                    Role = message.Role,
                    Content =  message.Content
                }).ToList();
        }
    }
}
