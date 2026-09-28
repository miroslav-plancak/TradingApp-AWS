using TradingApp.Infrastructure.Enums;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Interfaces.Retrieval
{
    public interface IFileExpansionService
    {
        Task<Dictionary<string, string>> DetermineFilesEligibleForExpansionAsync(
            List<RetrievedChunk> rerankedChunks, LlmQueryClassification routedLlmQueryResponse , int decomposedQueriesQuantity = 1);
        Task<Dictionary<string, string>> GetExistingFullFileContentsMapAsync(IEnumerable<string?> distinctFileNames);
    }
}
