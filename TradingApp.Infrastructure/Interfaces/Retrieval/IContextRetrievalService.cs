using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Interfaces.Retrieval
{
    public interface IContextRetrievalService
    {
        Task<RetrievalResult> RetrieveRelevantContextAsync
        (
            string userMessage,
            Guid conversationId
        );
        Task<List<RetrievedChunk>> RetrieveRelevantChunksAsync(string query);
        Task<string> GetFullFileContentAsync(string fileName);
    }
}
