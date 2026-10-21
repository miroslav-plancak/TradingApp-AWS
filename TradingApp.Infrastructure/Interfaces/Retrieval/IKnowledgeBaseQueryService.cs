using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Interfaces.Retrieval
{
    public interface IKnowledgeBaseQueryService
    {
        Task<List<RetrievedChunk>> SearchKnnChunksAsync(string userMessage, CancellationToken ct = default);
        Task<List<RetrievedChunk>> SearchLexicalChunksAsync(string userMessage, CancellationToken ct = default);
        Task<Dictionary<string, string>> GetSourceFileContentsAsync(IEnumerable<string> sourceFiles, CancellationToken ct = default);
    }
}
