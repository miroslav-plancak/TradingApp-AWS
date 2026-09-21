using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Conversation;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services.Regular.Conversation;
using TradingApp.Business.Mappers;

namespace TradingApp.Business.Services.Regular.Conversation
{
    public class ConversationSummaryService : IConversationSummaryService
    {
        private readonly IConversationRepository _conversationRepository;
        private readonly ILogger<ConversationSummaryService> _logger;

        public ConversationSummaryService
        (
            IConversationRepository conversationRepository,
            ILogger<ConversationSummaryService> logger
        )
        {
            _conversationRepository = conversationRepository;
            _logger = logger;
        }

        public async Task<ConversationCompactionStateDTO> GetConversationCompactionStateAsync(Guid conversationId)
        {
            _logger.LogInformation("GetConversationCompactionStateAsyncStarted | ConversationId: {ConversationId}", conversationId);

            try
            {
                var conversationEntity = await _conversationRepository.GetConversationById(conversationId);

                if (conversationEntity == null)
                {
                    _logger.LogWarning("GetConversationCompactionStateAsyncNotFound | ConversationId: {ConversationId}", conversationId);
                    throw new KeyNotFoundException($"Conversation {conversationId} not found.");
                }

                var conversationDTO = ConversationMapper.ToConversationCompactionStateDTO(conversationEntity);

                _logger.LogInformation("GetConversationCompactionStateAsyncRetrieved  | ConversationId: {ConversationId} " +
                    "| CompactedUntilMessage: {CompactedUntilMessage}",
                  conversationDTO.ConversationId, conversationDTO.SummaryCoversMessagesUpTo);

                return conversationDTO;
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetConversationCompactionStateAsyncFailed | ConversationId: {ConversationId}", conversationId);
                throw new Exception($"Failed to retrieve conversation compaction state {conversationId}", ex);
            }
        }

        public async Task UpdateCompactedConversationSummaryAsync
        (
            Guid conversationId,
            string compactedSummary,
            DateTimeOffset lastMessageCoveredBySummary
        )
        {
            _logger.LogInformation("UpdateCompactedConversationSummaryAsyncStarted | ConversationId: {ConversationId}", conversationId);

            try
            {
                await _conversationRepository.UpdateConversationByConversationId(conversationId, compactedSummary, lastMessageCoveredBySummary);

                _logger.LogInformation("UpdateCompactedConversationSummaryAsyncSuccessful | ConversationId: {ConversationId}", conversationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UpdateCompactedConversationSummaryAsyncFailed | ConversationId: {ConversationId}", conversationId);
            }
        }
    }
}
