using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Interfaces.Retrieval
{
    public interface IContextRetrievalService
    {
        Task<List<RetrievedChunk>> RetrieveRelevantChunksAsync(string query, CancellationToken ct = default);
        Task<string> GetFullFileContentAsync(string fileName, CancellationToken ct = default);
    }
}
