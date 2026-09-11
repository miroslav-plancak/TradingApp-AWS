using TradingApp.Infrastructure.Enums;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Helpers.Retrieval
{
    public static class ChunkFiltering
    {
        public static List<RetrievedChunk> CapChunksPerFile
        (
            List<RetrievedChunk> retrievedChunks,
            LlmQueryClassification routedLlmQueryResponse
        )
        {
            return retrievedChunks
                     .GroupBy(x => x.SourceFile ?? string.Empty)
                     .SelectMany(group => group
                     .Take(MaxChunksPerFile(routedLlmQueryResponse)))
                     .ToList();
        }

        public static List<RetrievedChunk> ExcludeChunksCoveredByExpandedFiles
        (
            List<RetrievedChunk> retrievedChunks,
            Dictionary<string, string> filesEligibleForExpansion
        )
        {
            return retrievedChunks
                    .Where(x => !filesEligibleForExpansion.ContainsKey(x.SourceFile ?? string.Empty))
                    .ToList();
        }

        private static int MaxChunksPerFile(LlmQueryClassification routedLlmQueryResponse)
        {
            return RetrievalPolicyResolver.ResolvePolicyValue(routedLlmQueryResponse, x => x.MaxChunksPerFile);
        }
       
      
    }
}
