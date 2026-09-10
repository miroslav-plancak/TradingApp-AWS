using System.Text.Json.Serialization;

namespace TradingApp.Infrastructure.Models.ConversationMemory
{
    public class ArbiterResponse
    {
        [JsonPropertyName("chunkKeys")]
        public List<string> ChunkKeys { get; set; } = [];
    }
}
