using System;
using System.Threading.Tasks;
using TradingApp.Domain.Models.Entities.Conversation;

namespace TradingApp.Business.Interfaces.Repositories
{
    public interface IConversationRepository
    {
        Task<Conversation> CreateConversationAsync(Conversation conversation, Guid? clientRequestId);
        Task<Conversation> GetConversationById(Guid conversationId);
    }
}
