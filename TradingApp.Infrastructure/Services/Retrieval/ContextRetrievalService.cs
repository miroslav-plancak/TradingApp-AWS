using Microsoft.Extensions.Logging;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.Retrieval;
using TradingApp.Infrastructure.Models.ConversationMemory;
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

        public async Task<List<RetrievedChunk>> RetrieveRelevantChunksAsync(string query)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query)) return [];

                var routedLlmQueryResponse = await _queryRoutingService.LlmQueryRouteAsync(query);

                var retrievedKNNChunks = await _knowledgeBaseQueryService.SearchKnnChunksAsync(query);

                var retrievedLexicalChunks = await _knowledgeBaseQueryService.SearchLexicalChunksAsync(query);

                var unifiedChunks = ChunkFusion.UnifyChunksFromBothSearchQueries(retrievedKNNChunks, retrievedLexicalChunks);

                var (knnChunksRankMap, lexicalChunksRankMap) = ChunkFusion.ComputeChunksRankMaps(retrievedKNNChunks, retrievedLexicalChunks);

                var unifiedChunksSortedByRrfScore = ChunkFusion.SortUnifiedChunksByRrfScore(unifiedChunks, knnChunksRankMap, lexicalChunksRankMap);

                var rerankedChunks = await _chunkRerankingService.RerankRetrievedChunksAsync(query, unifiedChunksSortedByRrfScore);

                rerankedChunks.RemoveAll(chunk => chunk.RelevanceScore < RelevanceFloor);

                var cappedChunks = ChunkFiltering.CapChunksPerFile(rerankedChunks, routedLlmQueryResponse);

                var distinctCappedChunks = DedupCombinedCappedChunks(cappedChunks);

                var fullFilesMap = await _fileExpansionService.GetExistingFullFileContentsMapAsync(distinctCappedChunks.Select(x => x.SourceFile));

                foreach (var chunk in distinctCappedChunks)
                {
                    chunk.IsFullFileIndexed = fullFilesMap.ContainsKey(chunk.SourceFile ?? string.Empty);
                }

                await _fileDebugLogger.LogSectionAsync("3-rag-final-context", $"Query: {query}",
                   RetrievalResultLogFormatter.FormatRetrievalResultIntoFileLog(new RetrievalResult { ChunkFallbacks = distinctCappedChunks }));

                return distinctCappedChunks;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure occurred while retrieving chunks for query: {Query}", query);
                return [];
            }
        }

        public async Task<string> GetFullFileContentAsync(string fileName)
        {
            var fileContents = await _knowledgeBaseQueryService.GetSourceFileContentsAsync(new[] { fileName });
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
        //TODO: check if we need these
        private static RetrievalResult BuildReorderedReusableRetrievalResult(ReusableConversationArtifacts reusableConversationContent)
        {
            var reusableChunks = RetrievalResultMapping.ToRetrievedChunks(reusableConversationContent.ConversationChunks);
            var fullFileContents = RetrievalResultMapping.ToFullFileContents(reusableConversationContent.ConversationFullFiles);

            return new RetrievalResult
            {
                ChunkFallbacks = ChunkReordering.ReorderChunksToUShape(
                    reusableChunks.Where(x => !fullFileContents.ContainsKey(x.SourceFile ?? string.Empty)).ToList()),
                FullFileContents = ChunkReordering.ReorderFullFilesToUShape(fullFileContents, reusableChunks)
            };
        }

        private void LogRedisSearchResults
        (
            List<RetrievedChunk> filteredRetrievedChunks,
            Dictionary<string, string> filesEligibleForExpansion,
            string userMessage
        )
        {
            _logger.LogInformation("Query: {userMessage}", userMessage);

            foreach (var chunk in filteredRetrievedChunks)
            {
                _logger.LogInformation("SourceFile: {SourceFile} | knnScore={knnScore} | lexicalScore={lexicalScore} | relevanceScore={relevanceScore}",
                    chunk.SourceFile?.ToString(), chunk.KnnScore?.ToString(), chunk.LexicalScore?.ToString(), chunk.RelevanceScore.ToString());
            }

            foreach (var expandedFile in filesEligibleForExpansion.Keys)
            {
                _logger.LogInformation("Expanded full file: {SourceFile}", expandedFile);
            }
        }
    }
}
