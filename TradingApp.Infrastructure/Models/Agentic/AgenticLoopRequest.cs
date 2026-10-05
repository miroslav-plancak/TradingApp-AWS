using Anthropic.Models.Messages;

namespace TradingApp.Infrastructure.Models.Agentic
{
    public class AgenticLoopRequest
    {
        public string UserMessage { get; set; } = string.Empty;
        public Guid ConversationId { get; set; }
        public required IReadOnlyList<MessageParam> ConversationMessagesHistory { get; set; }
        public string? CompactedSummary { get; set; } 
    }
}
