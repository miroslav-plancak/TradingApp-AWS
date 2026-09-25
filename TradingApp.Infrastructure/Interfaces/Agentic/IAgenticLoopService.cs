using Anthropic.Models.Messages;

namespace TradingApp.Infrastructure.Interfaces.Agentic
{
    public interface IAgenticLoopService
    {
        Task<string?> RunAgenticLoopAsync
        (
            string userMessage,
            IReadOnlyList<MessageParam> conversationMessagesHistory,
            string? compactedSummary
        );
    }
}
