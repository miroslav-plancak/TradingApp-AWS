using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Conversation;
using TradingApp.Business.DTOs.ConversationMessage;

namespace TradingApp.Business.Interfaces.Services
{
    public interface IConversationService
    {
        Task<CreatedConversationResponseDTO> CreateConversationAsync
        (
            string userMessage,
            Guid? clientRequestId
        );
        Task<CreatedConversationResponseDTO> GetConversationByIdAsync(Guid conversationId);
        Task<bool> DeleteConversationByIdAsync(Guid conversationId);
        Task<CreatedConversationMessageResponseDTO> CreateConversationMessageAsync(CreateConversationMessageRequestDTO request);
        Task<List<ConversationMessageDTO>> GetConversationMessagesAsync(Guid conversationId);
    }
}