using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Interfaces
{
    public interface IChunkIngestionService
    {
        List<ChunkRecord> ReadAndChunkSourceFiles(string[] sourceFiles);
        Task<List<ChunkRecord>> EmbedChunkedRecordsAsync(List<ChunkRecord> chunkedRecords);
        Task PersistChunkedRecordsToRedisAsync(List<ChunkRecord> chunkedRecords);
    }
}
