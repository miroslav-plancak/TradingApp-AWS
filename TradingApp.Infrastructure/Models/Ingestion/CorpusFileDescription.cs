using System.Text.Json.Serialization;

namespace TradingApp.Infrastructure.Models.Ingestion
{
    public class CorpusFileDescription
    {
        [JsonPropertyName("name")]
        public required string Name { get; set; }

        [JsonPropertyName("description")]
        public required string Description { get; set; }
    }
}
