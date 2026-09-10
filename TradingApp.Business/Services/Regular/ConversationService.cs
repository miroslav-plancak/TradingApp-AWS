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

        public async Task<CreatedConversationResponseDTO> CreateConversationAsync(string userQuery, Guid? clientRequestId)
        {
            var conversationName = GenerateConversationName(userQuery);

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

        private static string GenerateConversationName(string userQuestion)
        {
            if (string.IsNullOrWhiteSpace(userQuestion))
                return "New Conversation";

            var words = userQuestion.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

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

                var orderDTO = ConversationMapper.ToCreatedConversationResponseDTO(conversationEntity);

                _logger.LogInformation("GetConversationByIdAsyncRetrieved  | ConversationId: {ConversationId} " +
                    "| ConversationName: {ConversationName}",
                  orderDTO.ConversationId, orderDTO.Name);

                return orderDTO;
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

        public async Task<List<ConversationMessageDTO>> GetConversationMessagesAsync(Guid conversationId)
        {
            _logger.LogInformation("ConversationMessagesFetchingStarted | ConversationId: {ConversationId}", conversationId);

            try
            {
                var conversationMessages = await _conversationRepository.GetConversationMessagesAsync(conversationId);

                var anthropicMessages = ConversationMessageMapper.ToConversationMessagesDTO(conversationMessages);

                _logger.LogInformation("ConversationMessagesFetchingSuccessful | ConversationId: {ConversationId}", conversationId);

                return anthropicMessages;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConversationMessagesFetchingFailed  | Error: {Message}", ex.Message);

                throw new Exception("Failed to fetch conversation messages", ex);
            }
        }
    }
}
