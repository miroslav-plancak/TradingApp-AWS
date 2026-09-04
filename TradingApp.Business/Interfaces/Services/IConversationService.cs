using System;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Conversation;

namespace TradingApp.Business.Interfaces.Services
{
    public interface IConversationService
    {
        Task<CreatedConversationResponseDTO> CreateConversationAsync(string userQuery, Guid? clientRequestId);
        Task<CreatedConversationResponseDTO> GetConversationByIdAsync(Guid conversationId);
    }
}