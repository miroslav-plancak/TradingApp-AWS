using TradingApp.Infrastructure.Models.Retrieval;
using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Interfaces.Retrieval
{
    public interface IKnowledgeBaseQueryService
    {
        Task<List<RetrievedChunk>> SearchKnnChunksAsync(string userQuery);
        Task<List<RetrievedChunk>> SearchLexicalChunksAsync(string userQuery);
        Task<Dictionary<string, string>> GetSourceFileContentsAsync(IEnumerable<string> sourceFiles);
    }
}
