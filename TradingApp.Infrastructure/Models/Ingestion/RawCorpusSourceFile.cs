namespace TradingApp.Infrastructure.Models.Ingestion
{
    public class RawCorpusSourceFile
    {
        public required string Name { get; set; }
        public required string Content { get; set; }
    }
}
