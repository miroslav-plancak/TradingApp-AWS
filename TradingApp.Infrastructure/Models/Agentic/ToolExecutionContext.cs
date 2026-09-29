using System.Text.Json;
using TradingApp.Infrastructure.Enums;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Models.Agentic
{
    public class ToolExecutionContext
    {
        public Guid ConversationId { get; set; }
        public string ToolUseId { get; set; } = string.Empty;
        public AgenticTool ToolName { get; set; }
        public required IReadOnlyDictionary<string, JsonElement> Input { get; set; } 
        public HashSet<string> SeenChunkKeys { get; set; } = [];
        public Dictionary<string, List<RetrievedChunk>> ChunksByToolUseId { get; set; } = [];
    }
}
