using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Conversation;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services.Regular.Conversation;
using TradingApp.Business.Mappers;

namespace TradingApp.Business.Services.Regular.Conversation
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

     
    }
}
