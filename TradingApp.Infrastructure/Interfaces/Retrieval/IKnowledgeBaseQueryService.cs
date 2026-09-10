using TradingApp.Infrastructure.Models.Retrieval;
using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Interfaces.Retrieval
{
    public interface IKnowledgeBaseQueryService
    {
        Task<List<RetrievedChunk>> SearchKnnChunksAsync(string userMessage);
        Task<List<RetrievedChunk>> SearchLexicalChunksAsync(string userMessage);
        Task<Dictionary<string, string>> GetSourceFileContentsAsync(IEnumerable<string> sourceFiles);
    }
}
