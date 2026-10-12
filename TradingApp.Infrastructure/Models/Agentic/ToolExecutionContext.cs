using System.Text.Json;
using TradingApp.Infrastructure.Enums;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Models.Agentic
{
    public class ToolExecutionContext
    {
        public Guid ConversationId { get; set; }
        public string ToolUseId { get; set; } = string.Empty;
        public int ToolCallNumber { get; set; }
        public AgenticTool ToolName { get; set; }
        public required IReadOnlyDictionary<string, JsonElement> Input { get; set; }
        public required object SeenChunkKeysLock { get; set; } 
        public required HashSet<string> SeenChunkKeys { get; set; } = [];
        public required object ChunksByToolUseIdLock { get; set; } 
        public required Dictionary<string, List<RetrievedChunk>> ChunksByToolUseId { get; set; } = [];
        public int CallNumber { get; set; }
    }
}
