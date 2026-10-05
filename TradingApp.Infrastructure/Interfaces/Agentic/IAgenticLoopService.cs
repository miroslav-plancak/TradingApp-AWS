using Anthropic.Models.Messages;

namespace TradingApp.Infrastructure.Interfaces.Agentic
{
    public interface IAgenticLoopService
    {
        Task<string?> RunAgenticLoopAsync
        (
            string userMessage,
            Guid conversationId,
            IReadOnlyList<MessageParam> conversationMessagesHistory,
            string? compactedSummary
        );
        Task<string> HandleGetAllIndexedFileNames();
    }
}
