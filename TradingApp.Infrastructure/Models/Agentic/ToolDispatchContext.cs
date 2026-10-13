using Anthropic.Models.Messages;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Models.Agentic
{
    public class ToolDispatchContext
    {
        public required AgenticLoopRequest Request { get; set; }
        public required Message Response { get; set; }
        public required object AssistantContentLock { get; set; }
        public required List<ContentBlockParam> AssistantContent { get; set; }
        public required object ToolResultsContentLock { get; set; }
        public required List<ContentBlockParam> ToolResultsContent { get; set; }
        public required int ToolCallNumber;
        public required int SearchKnowledgeBaseToolCounter;
        public required int ApiCallNumber { get; set; }
        public required object SeenChunkKeysLock { get; set; }
        public required HashSet<string> SeenChunkKeys { get; set; }
        public required object ChunksByToolUseIdLock { get; set; }
        public required Dictionary<string, List<RetrievedChunk>> ChunksByToolUseId { get; set; }
        public required List<MessageParam> Messages { get; set; }
    }
}
