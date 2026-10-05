using Anthropic.Models.Messages;

namespace TradingApp.Infrastructure.Interfaces.ConversationMemory
{
    public interface IConversationCompactorService
    {
        Task CompactConversationAsync(Guid conversationId, Usage usage, long maxTokens);
    }
}
