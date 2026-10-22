using Microsoft.Extensions.Logging;
using TradingApp.Business.Interfaces.Services.Regular.Conversation;
using TradingApp.Infrastructure.Interfaces.ConversationMemory;

namespace TradingApp.Infrastructure.Services.ConversationMemory
{
    public class ConversationFullDeletionService : IConversationFullDeletionService
    {
        private readonly ILogger<ConversationFullDeletionService> _logger;
        private readonly IConversationService _conversationService;
        private readonly IConversationMessageService _conversationMessageService;
        private readonly IConversationChunkService _conversationChunkService;
        private readonly IConversationFullFileService _conversationFullFileService;

        public ConversationFullDeletionService
        (
            ILogger<ConversationFullDeletionService> logger,
            IConversationService conversationService,
            IConversationMessageService conversationMessageService,
            IConversationChunkService conversationChunkService,
            IConversationFullFileService conversationFullFileService
        )
        {
            _logger = logger;
            _conversationService = conversationService;
            _conversationMessageService = conversationMessageService;
            _conversationChunkService = conversationChunkService;
            _conversationFullFileService = conversationFullFileService;
        }

        public async Task<bool> DeleteConversationAndAllItsEntitiesAsync(Guid conversationId)
        {
            _logger.LogInformation("ConversationFullDeletionStarted | ConversationId: {ConversationId}", conversationId);

            try
            {
                var isConversationDeleted = await _conversationService.DeleteConversationByIdAsync(conversationId);

                if (!isConversationDeleted)
                {
                    _logger.LogWarning("ConversationFullDeletionAbortedConversationNotDeleted | ConversationId: {ConversationId}", conversationId);
                    return false;
                }

                await _conversationMessageService.DeleteConversationMessagesByConversationIdAsync(conversationId);
                await _conversationChunkService.DeleteConversationChunksByConversationIdAsync(conversationId);
                await _conversationFullFileService.DeleteConversationFullFilesByConversationIdAsync(conversationId);

                _logger.LogInformation("ConversationFullDeletionSuccessful | ConversationId: {ConversationId}", conversationId);

                return true;
            }
            catch (KeyNotFoundException)
            {
                _logger.LogWarning("ConversationFullDeletionSkipped | ConversationAlreadyGone | ConversationId: {ConversationId}", conversationId);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConversationFullDeletionFailed | ConversationId: {ConversationId} | Error: {Message}", conversationId, ex.Message);
                return false;
            }
        }
    }
}
