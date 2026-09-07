using System;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.ConversationMessage;
using TradingApp.Domain.Models.Entities.Conversation;
using TradingApp.Domain.Models.Entities.ConversationMessage;

namespace TradingApp.Business.Interfaces.Repositories
{
    public interface IConversationRepository
    {
        Task<Conversation> CreateConversationAsync(Conversation conversation, Guid? clientRequestId);
        Task<Conversation> GetConversationById(Guid conversationId);
        Task<bool> DeleteConversationByIdAsync(Guid conversationId);
        Task<ConversationMessage> CreateConversationMessageAsync(CreateConversationMessageRequestDTO request);
    }
}
