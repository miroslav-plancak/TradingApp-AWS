using Microsoft.Extensions.Logging;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.Retrieval;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Services.Retrieval
{
    public class ContextRetrievalService : IContextRetrievalService
    {
        private readonly ILogger<ContextRetrievalService> _logger;
        private readonly IQueryRoutingService _queryRoutingService;
        private readonly IKnowledgeBaseQueryService _knowledgeBaseQueryService;
        private readonly IChunkRerankingService _chunkRerankingService;
        private readonly IFileDebugLogger _fileDebugLogger;
        private readonly IFileExpansionService _fileExpansionService;
       
        private const double RelevanceFloor = 0.53;

        public ContextRetrievalService
        (
            ILogger<ContextRetrievalService> logger,
            IQueryRoutingService queryRoutingService,
            IKnowledgeBaseQueryService knowledgeBaseQueryService,
            IChunkRerankingService chunkRerankingService,
            IFileDebugLogger fileDebugLogger,
            IFileExpansionService fileExpansionService
        )
        {
            _logger = logger;
            _queryRoutingService = queryRoutingService;
            _knowledgeBaseQueryService = knowledgeBaseQueryService;
            _chunkRerankingService = chunkRerankingService;
            _fileDebugLogger = fileDebugLogger;
            _fileExpansionService = fileExpansionService;
        }

        public async Task<List<RetrievedChunk>> RetrieveRelevantChunksAsync(string query, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query)) return [];

                var routedLlmQueryResponse = await _queryRoutingService.LlmQueryRouteAsync(query, ct);

                var retrievedKNNChunks = await _knowledgeBaseQueryService.SearchKnnChunksAsync(query, ct);

                var retrievedLexicalChunks = await _knowledgeBaseQueryService.SearchLexicalChunksAsync(query, ct);

                var unifiedChunks = ChunkFusion.UnifyChunksFromBothSearchQueries(retrievedKNNChunks, retrievedLexicalChunks);

                var (knnChunksRankMap, lexicalChunksRankMap) = ChunkFusion.ComputeChunksRankMaps(retrievedKNNChunks, retrievedLexicalChunks);

                var unifiedChunksSortedByRrfScore = ChunkFusion.SortUnifiedChunksByRrfScore(unifiedChunks, knnChunksRankMap, lexicalChunksRankMap);

                var rerankedChunks = await _chunkRerankingService.RerankRetrievedChunksAsync(query, unifiedChunksSortedByRrfScore, ct);

                rerankedChunks.RemoveAll(chunk => chunk.RelevanceScore < RelevanceFloor);

                var cappedChunks = ChunkFiltering.CapChunksPerFile(rerankedChunks, routedLlmQueryResponse);

                var distinctCappedChunks = DedupCombinedCappedChunks(cappedChunks);

                var fullFilesMap = await _fileExpansionService.GetExistingFullFileContentsMapAsync(distinctCappedChunks.Select(x => x.SourceFile), ct);

                foreach (var chunk in distinctCappedChunks)
                {
                    chunk.IsFullFileIndexed = fullFilesMap.ContainsKey(chunk.SourceFile ?? string.Empty);
                }

                await _fileDebugLogger.LogSectionAsync("3-rag-final-context", $"Query: {query}",
                   RetrievalResultLogFormatter.FormatRetrievalResultIntoFileLog(new RetrievalResult { ChunkFallbacks = distinctCappedChunks }));

                return distinctCappedChunks;
            }
            catch(OperationCanceledException osEx) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation(osEx, "ChunkRetrievalCancelled | Query: {Query}", query);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure occurred while retrieving chunks for query: {Query}", query);
                return [];
            }
        }

        public async Task<string> GetFullFileContentAsync(string fileName, CancellationToken ct)
        {
            var fileContents = await _knowledgeBaseQueryService.GetSourceFileContentsAsync(new[] { fileName },ct);
            var content = fileContents.TryGetValue(fileName, out var value) ? value : string.Empty;
            return content;
        }

        private List<RetrievedChunk> DedupCombinedCappedChunks(List<RetrievedChunk> combinedCappedChunks)
        {
            return combinedCappedChunks
                .GroupBy(x => x.Key)
                .Select(group => group.OrderByDescending(x => x.RelevanceScore)
                .First())
                .ToList();
        }
    }
}
