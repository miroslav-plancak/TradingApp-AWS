using Anthropic.Models.Messages;

namespace TradingApp.Infrastructure.Models.Agentic
{
    public class AgenticLoopRequest
    {
        public string UserMessage { get; set; } = string.Empty;
        public Guid ConversationId { get; set; }
        public required IReadOnlyList<MessageParam> ConversationMessagesHistory { get; set; }
        public string? CompactedSummary { get; set; }
        public required Func<long, Task> MaxTokensHandler { get; set; }
        public required Func<MessageDeltaUsage, Task> UsageHandler { get; set; }
        public required Func<string, Task> ToolCalledHandler { get; set; }
        public required Func<string, Task> ResponseTruncatedHandler { get; set; }
        public required Func<StopReason, Task> UnexpectedStopReasonHandler { get; set; }
        public required CancellationToken CancellationToken { get; set; }
    }
}
