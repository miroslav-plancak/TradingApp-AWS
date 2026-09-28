using TradingApp.Infrastructure.Models.Retrieval;

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
