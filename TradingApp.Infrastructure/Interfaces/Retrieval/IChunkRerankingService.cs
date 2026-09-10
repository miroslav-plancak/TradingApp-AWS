using TradingApp.Infrastructure.Models.Retrieval;
using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Interfaces.Retrieval
{
    public interface IChunkRerankingService
    {
        Task<List<RetrievedChunk>> RerankRetrievedChunksAsync
        (
            string userMessage,
            List<RetrievedChunk> retrievedChunks
        );
    }
}
