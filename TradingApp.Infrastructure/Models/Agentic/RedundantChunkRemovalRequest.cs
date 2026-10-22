using Anthropic.Models.Messages;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Models.Agentic
{
    public class RedundantChunkRemovalRequest
    {
        public required string FileName { get; set; }
        public required List<MessageParam> Messages { get; set; }
        public required object ChunksByToolUseIdLock { get; set; }
        public required Dictionary<string, List<RetrievedChunk>> ChunksByToolUseId { get; set; }
    }
}
