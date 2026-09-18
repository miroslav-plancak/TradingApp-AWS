using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Conversation;
using TradingApp.Business.DTOs.ConversationMessage;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Business.Mappers;
using TradingApp.Domain.Models.Entities.ConversationMessage;
using TradingApp.Domain.Models.Enums;

namespace TradingApp.Business.Services.Regular
{
    public class ConversationService : IConversationService
    {
        private readonly IConversationRepository _conversationRepository;
        private readonly ILogger<ConversationService> _logger;

        public ConversationService
        (
            IConversationRepository conversationRepository,
            ILogger<ConversationService> logger
        )
        {
            _conversationRepository = conversationRepository;
            _logger = logger;
        }

        public async Task<List<CreatedConversationResponseDTO>> GetConversationsAsync()
        {
            _logger.LogInformation("GetConversationsStarted");

            try
            {
                var conversationEntities = await _conversationRepository.GetConversationsAsync();

                var createdConversationResponseDTOs = ConversationMapper.ToCreatedConversationResponseDTOs(conversationEntities);

                _logger.LogInformation("GetConversationsSuccessful | Conversations fetched count: {Count}", createdConversationResponseDTOs.Count());

                return createdConversationResponseDTOs.ToList();

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetConversationsFailed  | Error: {Message}", ex.Message);

                throw new Exception("Failed to fetch conversations", ex);
            }

        }

        public async Task<CreatedConversationResponseDTO> CreateConversationAsync
        (
            string userMessage,
            Guid? clientRequestId
        )
        {
            var conversationName = GenerateConversationName(userMessage);

            _logger.LogInformation("ConversationCreationStarted | ConversationName: {ConversationName}", conversationName);

            try
            {
                var conversationEntityRequest = ConversationMapper.ToEntity(conversationName);

                var conversation = await _conversationRepository.CreateConversationAsync(conversationEntityRequest, clientRequestId);

                var createdConversationResponseDTO = ConversationMapper.ToCreatedConversationResponseDTO(conversation);

                _logger.LogInformation("ConversationCreationSuccessful | ConversationId: {ConversationId}", conversation.Id);

                return createdConversationResponseDTO;

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConversationCreationFailed  | Error: {Message}", ex.Message);

                throw new Exception("Failed to create conversation", ex);
            }

        }

        private static string GenerateConversationName(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage))
                return "New Conversation";

            var words = userMessage.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

            return string.Join(" ", words.Take(4));
        }

        public async Task<CreatedConversationResponseDTO> GetConversationByIdAsync(Guid conversationId)
        {
            _logger.LogInformation("GetConversationByIdAsyncStarted | ConversationId: {ConversationId}", conversationId);

            try
            {
                var conversationEntity = await _conversationRepository.GetConversationById(conversationId);

                if (conversationEntity == null)
                {
                    _logger.LogWarning("GetConversationByIdAsyncNotFound | ConversationId: {ConversationId}", conversationId);
                    throw new KeyNotFoundException($"Conversation {conversationId} not found.");
                }

                var conversationDTO = ConversationMapper.ToCreatedConversationResponseDTO(conversationEntity);

                _logger.LogInformation("GetConversationByIdAsyncRetrieved  | ConversationId: {ConversationId} " +
                    "| ConversationName: {ConversationName}",
                  conversationDTO.ConversationId, conversationDTO.Name);

                return conversationDTO;
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetConversationByIdAsyncFailed | ConversationId: {ConversationId}", conversationId);
                throw new Exception($"Failed to retrieve conversation {conversationId}", ex);
            }
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

        public async Task<bool> DeleteConversationByIdAsync(Guid conversationId)
        {
            _logger.LogInformation("DeleteConversation | ConversationId: {ConversationId}", conversationId);

            try
            {
                var orderEntity = await _conversationRepository.GetConversationById(conversationId);

                if (orderEntity == null)
                {
                    _logger.LogWarning("ConversationNotFoundForDeletion | ConversationId: {ConversationId}", conversationId);
                    throw new KeyNotFoundException($"Conversation {conversationId} not found.");
                }

                var deleted = await _conversationRepository.DeleteConversationByIdAsync(conversationId);

                _logger.LogInformation("ConversationDeleted | ConversationId: {ConversationId}", conversationId);

                return deleted;
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeleteConversationFailed | ConversationId: {ConversationId}", conversationId);
                throw new Exception($"Failed to delete conversation {conversationId}", ex);
            }
        }

        public async Task<CreatedConversationMessageResponseDTO> CreateConversationMessageAsync(CreateConversationMessageRequestDTO request)
        {
            _logger.LogInformation("ConversationMessageCreationStarted | ConversationId: {ConversationId}", request.ConversationId);

            try
            {
                var createConversationMessageRequestDTO = ConversationMessageMapper.ToCreateConversationMessageRequestDTO(request);
                var conversationMessage = await _conversationRepository.CreateConversationMessageAsync(createConversationMessageRequestDTO);

                var createdConversationMessageResponseDTO = ConversationMessageMapper.ToCreatedConversationMessageResponseDTO(conversationMessage);

                _logger.LogInformation("ConversationMessageCreationSuccessful | ConversationId: {ConversationId} | MessageId: {MessageId}",
                    request.ConversationId, conversationMessage.Id);

                return createdConversationMessageResponseDTO;

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConversationMessageCreationFailed  | Error: {Message}", ex.Message);

                throw new Exception("Failed to create conversation message", ex);
            }
        }

        public async Task<List<ConversationHistoryMessageDTO>> GetConversationMessagesAsync(Guid conversationId, DateTimeOffset? createdAfter)
        {
            _logger.LogInformation("ConversationMessagesFetchingStarted | ConversationId: {ConversationId}", conversationId);

            try
            {
                var conversationMessages = await _conversationRepository.GetConversationMessagesAsync(conversationId, createdAfter);

                var anthropicMessages = ConversationMessageMapper.ToConversationHistoryMessageDTOs(conversationMessages);

                _logger.LogInformation("ConversationMessagesFetchingSuccessful | ConversationId: {ConversationId}", conversationId);

                return anthropicMessages;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConversationMessagesFetchingFailed  | Error: {Message}", ex.Message);

                throw new Exception("Failed to fetch conversation messages", ex);
            }
        }

        public async Task<List<ConversationHistoryMessageDTO>> GetConversationMessagesForCompactionAsync
        (
            ConversationCompactionStateDTO conversationDTO, 
            int postCompactionTokenBudget
        )
        {
            _logger.LogInformation("GetConversationMessagesForCompactionAsyncStarted | ConversationId: {ConversationId}", conversationDTO.ConversationId);

            try
            {   
                var allConversationMessages = await _conversationRepository.GetConversationMessagesAsync(
                    conversationDTO.ConversationId, conversationDTO.SummaryCoversMessagesUpTo);

                if(!allConversationMessages.Any())
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

                _logger.LogInformation("GetConversationMessagesForCompactionAsyncSuccessful | ConversationId: {ConversationId}", conversationDTO.ConversationId);

                return messagesToCompactDtos;

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetConversationMessagesForCompactionAsyncFailed  | Error: {Message}", ex.Message);

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
