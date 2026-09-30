namespace TradingApp.Infrastructure.Models.Retrieval
{
    public class RetrievedChunk
    {
        public string? Key { get; set; }
        public int ChunkIndex { get; set; }
        public int TotalChunkCount { get; set; }
        public string? SourceFile { get; set; }
        public string? Content { get; set; }
        public double? KnnScore { get; set; }
        public double? LexicalScore { get; set; }
        public double RelevanceScore { get; set; }
        public double ReciprocalRankFusionScore { get; set; }
        public bool IsFullFileIndexed { get; set; }
        public bool IsFullFileReconstructable { get; set; }
    }
}
