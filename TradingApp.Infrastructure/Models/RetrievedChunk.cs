namespace TradingApp.Infrastructure.Models
{
    public class RetrievedChunk
    {
        public string? Key { get; set; }
        public string? SourceFile { get; set; }
        public string? Content { get; set; }
        public double? KnnScore { get; set; }
        public double? LexicalScore { get; set; }
        public double RelevanceScore { get; set; }
        public double ReciprocalRankFusionScore { get; set; }
    }
}
