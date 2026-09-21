using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.ConversationMessage;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services.Regular.Conversation;
using TradingApp.Business.Mappers;

namespace TradingApp.Business.Services.Regular.Conversation
{
    public class ConversationMessageService : IConversationMessageService
    {
        private readonly IConversationRepository _conversationRepository;
        private readonly ILogger<ConversationMessageService> _logger;

        public ConversationMessageService
        (   
            IConversationRepository conversationRepository,
            ILogger<ConversationMessageService> logger
        )
        {
            _conversationRepository = conversationRepository;
            _logger = logger;
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
    }
}
