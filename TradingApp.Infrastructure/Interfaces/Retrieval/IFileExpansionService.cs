using TradingApp.Infrastructure.Enums;
using TradingApp.Infrastructure.Models;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Interfaces.Retrieval
{
    public interface IFileExpansionService
    {
        Task<Dictionary<string, string>> DetermineFilesEligibleForExpansionAsync(
            List<RetrievedChunk> rerankedChunks, LlmQueryClassification routedLlmQueryResponse);
    }
}
