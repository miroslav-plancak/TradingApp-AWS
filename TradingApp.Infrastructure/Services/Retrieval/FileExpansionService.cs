using TradingApp.Infrastructure.Enums;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces.Retrieval;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Services.Retrieval
{
    public class FileExpansionService : IFileExpansionService
    {
        private readonly IKnowledgeBaseQueryService _knowledgeBaseQueryService;

        public FileExpansionService(IKnowledgeBaseQueryService knowledgeBaseQueryService)
        {
            _knowledgeBaseQueryService = knowledgeBaseQueryService;
        }

        public async Task<Dictionary<string, string>> DetermineFilesEligibleForExpansionAsync
        (
           List<RetrievedChunk> rerankedChunks,
           LlmQueryClassification routedLlmQueryResponse
        )
        {
            var fileOccurrenceMap = rerankedChunks.GroupBy(x => x.SourceFile ?? string.Empty).ToDictionary(g => g.Key, g => g.Count());

            var distinctFileNames = rerankedChunks.DistinctBy(x => x.SourceFile).Select(x => x.SourceFile);
            var filesWithFullContent = await GetExistingFullFileContentsMapAsync(distinctFileNames);


            var filesEligibleForExpansion = filesWithFullContent
                        .Where(kvp => fileOccurrenceMap.TryGetValue(kvp.Key, out var occurrences)
                            && occurrences >= MinimumOccurrenceThreshold(routedLlmQueryResponse))
                        .ToDictionary(x => x.Key, x => x.Value);

            return filesEligibleForExpansion;
        }

        private static int MinimumOccurrenceThreshold(LlmQueryClassification routedLlmQueryResponse)
        {
            return RetrievalPolicyResolver.ResolvePolicyValue(routedLlmQueryResponse, x => x.MinimumOccurrenceThreshold);
        }

        private async Task<Dictionary<string, string>> GetExistingFullFileContentsMapAsync(IEnumerable<string?> distinctFileNames)
        {
            var allFetchedFileContents = await _knowledgeBaseQueryService.GetSourceFileContentsAsync(distinctFileNames.OfType<string>());
            var filesWithContent = allFetchedFileContents.Where(x => x.Value != string.Empty).ToDictionary(x => x.Key, x => x.Value);
            return filesWithContent;
        }
    }
}
