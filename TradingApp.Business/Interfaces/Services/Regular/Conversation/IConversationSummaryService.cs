using System;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Conversation;

namespace TradingApp.Business.Interfaces.Services.Regular.Conversation
{
    public interface IConversationSummaryService
    {
        Task<ConversationCompactionStateDTO> GetConversationCompactionStateAsync(Guid conversationId);
        Task UpdateCompactedConversationSummaryAsync(Guid conversationId, string compactedSummary, DateTimeOffset lastMessageCoveredBySummary);
    }
}
