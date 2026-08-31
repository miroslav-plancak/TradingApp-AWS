using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Interfaces
{
    public interface IKnowledgeBaseQueryService
    {
        Task<List<RetrievedChunk>> SearchKnnChunksAsync(string userQuestion);
        Task<List<RetrievedChunk>> SearchLexicalChunksAsync(string userQuestion);
        Task<Dictionary<string, string>> GetSourceFileContentsAsync(IEnumerable<string> sourceFiles);
    }
}
