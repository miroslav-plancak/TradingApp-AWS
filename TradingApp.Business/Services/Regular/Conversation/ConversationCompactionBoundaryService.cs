using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Conversation;
using TradingApp.Business.DTOs.ConversationMessage;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services.Regular.Conversation;
using TradingApp.Business.Mappers;
using TradingApp.Domain.Models.Entities.ConversationMessage;
using TradingApp.Domain.Models.Enums;

namespace TradingApp.Business.Services.Regular.Conversation
{
    public class ConversationCompactionBoundaryService : IConversationCompactionBoundaryService
    {
        private readonly IConversationRepository _conversationRepository;
        private readonly ILogger<ConversationCompactionBoundaryService> _logger;

        public ConversationCompactionBoundaryService
        (
            IConversationRepository conversationRepository,
            ILogger<ConversationCompactionBoundaryService> logger
        )
        {

            _conversationRepository = conversationRepository;
            _logger = logger;
        }

        public async Task<List<ConversationHistoryMessageDTO>> GetMessagesForCompactionAsync
        (
           ConversationCompactionStateDTO conversationDTO,
           int postCompactionTokenBudget
        )
        {
            _logger.LogInformation("GetMessagesForCompactionAsyncStarted | ConversationId: {ConversationId}", conversationDTO.ConversationId);

            try
            {
                var allConversationMessages = await _conversationRepository.GetConversationMessagesAsync(
                    conversationDTO.ConversationId, conversationDTO.SummaryCoversMessagesUpTo);

                if (!allConversationMessages.Any())
                {
                    _logger.LogWarning("NoMessagesRetrievedForThisConversation: {ConversationId}", conversationDTO.ConversationId);
                    return [];
                }

                var lastFullTurnMessage = FindLastUserMessageWithinGivenThreshold(allConversationMessages, postCompactionTokenBudget);

                // NOTE: This is a guard against theoretical edgecase which can happen if Assistant's message token value,
                // which gets iterated through first, is greater than this method's 2nd param value passed by the caller.
                // Currently this can never happen because the Claude Sonnet 5 model that we are using, has a max ouput
                // per request capped at 128k tokens, which is the maximum message length that can exist in our system.
                // The caller of this method is currently supplying postCompactionTokenBudget = 350k. Which means that no single
                // assistant message can break the foreach loop leaving lastFullTurnMessage value at null and cause
                // NullReferenceException down the line, but this method should be resistant to the future changes to the
                // model we use and the caller passing a different value.
                if (lastFullTurnMessage == null)
                {
                    _logger.LogWarning("LastFullTurnMessage is null in conversation: {ConversationId}", conversationDTO.ConversationId);
                    return [];
                }

                var messagesToCompact = allConversationMessages.Where(x => x.CreatedAt < lastFullTurnMessage.CreatedAt);
                var messagesToCompactDtos = ConversationMessageMapper.ToConversationHistoryMessageDTOs(messagesToCompact);

                if(messagesToCompact.Count() == 0)
                {
                    _logger.LogInformation("GetMessagesForCompactionAsyncFoundNoMessagesToCompact ");

                    return messagesToCompactDtos;
                }

                _logger.LogInformation("GetMessagesForCompactionAsyncSuccessful | number of messages compacted: {CompactedMsgsCount}",
                    messagesToCompactDtos.Count);

                return messagesToCompactDtos;

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetMessagesForCompactionAsyncFailed  | Error: {Message}", ex.Message);

                throw new Exception("Failed to fetch conversation messages for compaction", ex);
            }

        }

        private static ConversationMessageDTO FindLastUserMessageWithinGivenThreshold(IEnumerable<ConversationMessage> allConversationMessages, int targetThreshold)
        {
            ConversationMessageDTO lastFullTurnMessage = null;
            var accumulatedTokens = 0;

            foreach (var message in allConversationMessages.Reverse())
            {
                if (!string.IsNullOrWhiteSpace(message.Body))
                {
                    accumulatedTokens += message.Body.Length / 4;
                }

                if (message.Role == ConversationMessageRole.User)
                {
                    lastFullTurnMessage = ConversationMessageMapper.ToConversationMessageDTO(message);
                }

                if (targetThreshold <= accumulatedTokens && message.Role != ConversationMessageRole.User)
                {
                    break;
                }
            }

            return lastFullTurnMessage;
        }
    }
}
