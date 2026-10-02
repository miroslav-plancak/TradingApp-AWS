namespace TradingApp.Infrastructure.Models.Ingestion
{
    public class ProcessedCorpusSourceFile
    {
        public required string Name { get; set; }
        public required string Description { get; set; }
        public bool IsFullyIndexed { get; set; }
    }
}
