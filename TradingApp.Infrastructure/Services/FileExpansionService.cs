using TradingApp.Infrastructure.Enums;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Services
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
           LlmQueryClassification routedLlmQUeryResponse
        )
        {
            var fileOccurrenceMap = rerankedChunks.GroupBy(x => x.SourceFile ?? string.Empty).ToDictionary(g => g.Key, g => g.Count());

            var distinctFileNames = rerankedChunks.DistinctBy(x => x.SourceFile).Select(x => x.SourceFile);
            var filesWithFullContent = await GetExistingFullFileContentsMapAsync(distinctFileNames);


            var filesEligibleForExpansion = filesWithFullContent
                        .Where(kvp => fileOccurrenceMap[kvp.Key] >= MinimumOccurenceTreshhold(routedLlmQUeryResponse))
                        .ToDictionary(x => x.Key, x => x.Value);

            return filesEligibleForExpansion;
        }

        private static int MinimumOccurenceTreshhold(LlmQueryClassification routedLlmQUeryResponse)
        {
            switch (routedLlmQUeryResponse)
            {
                case LlmQueryClassification.BROAD:
                    return 1;
                case LlmQueryClassification.NARROW:
                    return 2;
                default:
                    return 2;
            }
        }

        private async Task<Dictionary<string, string>> GetExistingFullFileContentsMapAsync(IEnumerable<string?> distinctFileNames)
        {
            var allFetchedFileContents = await _knowledgeBaseQueryService.GetSourceFileContentsAsync(distinctFileNames.OfType<string>());
            var filesWithContent = allFetchedFileContents.Where(x => x.Value != string.Empty).ToDictionary(x => x.Key, x => x.Value);
            return filesWithContent;
        }

   
    }
}
