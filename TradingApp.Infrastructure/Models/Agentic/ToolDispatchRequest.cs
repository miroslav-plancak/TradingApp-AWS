using Anthropic.Models.Messages;
using TradingApp.Infrastructure.Enums;

namespace TradingApp.Infrastructure.Models.Agentic
{
    public class ToolDispatchRequest
    {
        public required ToolDispatchContext Context { get; set; }
        public required ToolUseBlock ToolUseBlock { get; set; }
        public required bool IsKnownTool { get; set; }
        public required AgenticTool ToolName { get; set; }
    }
}
