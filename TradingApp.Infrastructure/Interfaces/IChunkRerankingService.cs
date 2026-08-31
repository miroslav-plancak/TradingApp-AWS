using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Interfaces
{
    public interface IChunkRerankingService
    {
        Task<List<RetrievedChunk>> RerankRetrievedChunksAsync(string userQuestion, List<RetrievedChunk> retrievedChunks);
    }
}
