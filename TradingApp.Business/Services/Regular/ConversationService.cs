using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Conversation;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Business.Mappers;
using TradingApp.Domain;

namespace TradingApp.Business.Services.Regular
{
    public class ConversationService : IConversationService
    {
        private readonly IConversationRepository _conversationRepository;
        private readonly TradingDbContext _tradingDbContext;
        private readonly ILogger<ConversationService> _logger;
        public ConversationService
        (
            IConversationRepository conversationRepository,
            TradingDbContext tradingDbContext, 
            ILogger<ConversationService> logger
        )
        {
            _conversationRepository = conversationRepository;
            _tradingDbContext = tradingDbContext;
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
                  orderDTO.ConversationId,  orderDTO.Name );

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
    }
}
