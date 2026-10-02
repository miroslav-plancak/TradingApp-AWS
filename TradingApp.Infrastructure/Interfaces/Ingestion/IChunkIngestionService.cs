using TradingApp.Infrastructure.Models.Ingestion;

namespace TradingApp.Infrastructure.Interfaces.Ingestion
{
    public interface IChunkIngestionService
    {
        Task<List<ChunkRecord>> ReadAndChunkSourceFiles(string[] sourceFiles);
        Task<List<ChunkRecord>> EmbedChunkedRecordsAsync(List<ChunkRecord> chunkedRecords);
        Task PersistChunkedRecordsToRedisAsync(List<ChunkRecord> chunkedRecords);
        Task PersistEntireSourceFilesCorpusAsync(string[] sourceFiles);
    }
}
