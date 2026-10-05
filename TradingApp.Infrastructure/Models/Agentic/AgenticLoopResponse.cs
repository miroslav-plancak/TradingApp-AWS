using Anthropic.Models.Messages;

namespace TradingApp.Infrastructure.Models.Agentic
{
    public class AgenticLoopResponse
    {
        public string MessageResponse { get; set; } = string.Empty;
        public Usage? TokenUsage { get; set; }
        public long MaxTokens { get; set; }
    }
}
