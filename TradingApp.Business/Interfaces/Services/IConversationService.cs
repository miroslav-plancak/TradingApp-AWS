using System;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Conversation;
using TradingApp.Business.DTOs.ConversationMessage;
using TradingApp.Domain.Models.Enums;

namespace TradingApp.Business.Interfaces.Services
{
    public interface IConversationService
    {
        Task<CreatedConversationResponseDTO> CreateConversationAsync(string userQuery, Guid? clientRequestId);
        Task<CreatedConversationResponseDTO> GetConversationByIdAsync(Guid conversationId);
        Task<bool> DeleteConversationByIdAsync(Guid conversationId);
        Task<CreatedConversationMessageResponseDTO> CreateConversationMessageAsync(Guid conversationId, Guid? clientRequestId, ConversationMessageRole role, string body);
    }
}