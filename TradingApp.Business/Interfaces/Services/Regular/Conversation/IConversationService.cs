using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Conversation;

namespace TradingApp.Business.Interfaces.Services.Regular.Conversation
{
    public interface IConversationService
    {
        Task<CreatedConversationResponseDTO> CreateConversationAsync(string userMessage, Guid? clientRequestId);
        Task<List<CreatedConversationResponseDTO>> GetConversationsAsync();
        Task<CreatedConversationResponseDTO> GetConversationByIdAsync(Guid conversationId);
        Task<bool> DeleteConversationByIdAsync(Guid conversationId);
    }
}
