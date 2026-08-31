using TradingApp.Infrastructure.Enums;
using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Interfaces
{
    public interface IFileExpansionService
    {
        Task<Dictionary<string, string>> DetermineFilesEligibleForExpansionAsync(
            List<RetrievedChunk> rerankedChunks,LlmQueryClassification routedLlmQUeryResponse);
    }
}
