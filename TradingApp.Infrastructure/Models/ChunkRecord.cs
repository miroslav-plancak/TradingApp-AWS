namespace TradingApp.Infrastructure.Models
{
    public class ChunkRecord
    {
        public required int Id { get; set; }
        public required string SourceFile { get; set; }
        public required string Content { get; set; }
        public float[] Embedding { get; set; } = [];
    }
}
