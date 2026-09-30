using System.Text.Json.Serialization;

namespace TradingApp.Infrastructure.Models.ConversationMemory
{
    public class ChunkArbiterResponse
    {
        [JsonPropertyName("chunkKeys")]
        public List<string> ChunkKeys { get; set; } = [];
    }
}
