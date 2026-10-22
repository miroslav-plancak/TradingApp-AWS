using TradingApp.Infrastructure.Interfaces.Retrieval;

namespace TradingApp.Infrastructure.Services.Retrieval
{
    public class FileExpansionService : IFileExpansionService
    {
        private readonly IKnowledgeBaseQueryService _knowledgeBaseQueryService;

        public FileExpansionService(IKnowledgeBaseQueryService knowledgeBaseQueryService)
        {
            _knowledgeBaseQueryService = knowledgeBaseQueryService;
        }

        public async Task<Dictionary<string, string>> GetExistingFullFileContentsMapAsync(IEnumerable<string?> distinctFileNames, CancellationToken ct)
        {
            var allFetchedFileContents = await _knowledgeBaseQueryService.GetSourceFileContentsAsync(distinctFileNames.OfType<string>(), ct);
            var filesWithContent = allFetchedFileContents.Where(x => x.Value != string.Empty).ToDictionary(x => x.Key, x => x.Value);
            return filesWithContent;
        }
    }
}
