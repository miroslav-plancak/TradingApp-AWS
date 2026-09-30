using System.Text.Json.Serialization;

namespace TradingApp.Infrastructure.Models.ConversationMemory
{
    public class FullFileArbiterResponse
    {
        [JsonPropertyName("sourceFiles")]
        public List<string> SourceFiles { get; set; } = [];
    }
}
