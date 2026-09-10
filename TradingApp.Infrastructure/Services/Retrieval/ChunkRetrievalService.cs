using Microsoft.Extensions.Logging;
using TradingApp.Infrastructure.Helpers;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Models;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces.ConversationMemory;
using TradingApp.Infrastructure.Interfaces.Retrieval;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Services.Retrieval
{
    public class ChunkRetrievalService : IChunkRetrievalService
    {
        private readonly ILogger<ChunkRetrievalService> _logger;
        private readonly IQueryRoutingService _queryRoutingService;
        private readonly IKnowledgeBaseQueryService _knowledgeBaseQueryService;
        private readonly IChunkRerankingService _chunkRerankingService;
        private readonly IFileDebugLogger _fileDebugLogger;
        private readonly IFileExpansionService _fileExpansionService;
        private readonly IConversationReuseService _conversationReuseService;

        private const double RelevanceFloor = 0.53;

        public ChunkRetrievalService
        (
            ILogger<ChunkRetrievalService> logger,
            IQueryRoutingService queryRoutingService,
            IKnowledgeBaseQueryService knowledgeBaseQueryService,
            IChunkRerankingService chunkRerankingService,
            IFileDebugLogger fileDebugLogger,
            IFileExpansionService fileExpansionService,
            IConversationReuseService conversationReuseService
        )
        {
            _logger = logger;
            _queryRoutingService = queryRoutingService;
            _knowledgeBaseQueryService = knowledgeBaseQueryService;
            _chunkRerankingService = chunkRerankingService;
            _fileDebugLogger = fileDebugLogger;
            _fileExpansionService = fileExpansionService;
            _conversationReuseService = conversationReuseService;
        }

        public async Task<RetrievalResult> RetrieveRelevantContextAsync
        (
            string userQuery,
            Guid conversationId
        )
        {
            try
            {
                var reusableConversationContent = await _conversationReuseService.TryRetrieveReusableConversationArtifactsAsync(conversationId, userQuery);

                if (reusableConversationContent.ConversationChunks.Count > 0)
                {
                    var reusableRetrievalResult = RetrievalResultMapping.ToRetrievalResult(reusableConversationContent);

                    _logger.LogInformation(
                        "Query: {userQuery} | Existing conversation chunk pool judged sufficient - skipping RAG pipeline",
                        userQuery);

                    await _fileDebugLogger.LogSectionAsync("3-arbiter-skip-context", $"Query: {userQuery}",
                        RetrievalResultLogFormatter.FormatRetrievalResultIntoFileLog(reusableRetrievalResult));

                    return reusableRetrievalResult;
                }

                var routedLlmQueryResponse = await _queryRoutingService.LlmQueryRouteAsync(userQuery);

                var retrievedKNNChunks = await _knowledgeBaseQueryService.SearchKnnChunksAsync(userQuery);

                var retrievedLexicalChunks = await _knowledgeBaseQueryService.SearchLexicalChunksAsync(userQuery);

                var unifiedChunks = ChunkFusion.UnifyChunksFromBothSearchQueries(retrievedKNNChunks, retrievedLexicalChunks);

                var (knnChunksRankMap, lexicalChunksRankMap) = ChunkFusion.ComputeChunksRankMaps(retrievedKNNChunks, retrievedLexicalChunks);

                var unifiedChunksSortedByRrfScore = ChunkFusion.SortUnifiedChunksByRrfScore(unifiedChunks, knnChunksRankMap, lexicalChunksRankMap);

                var rerankedChunks = await _chunkRerankingService.RerankRetrievedChunksAsync(userQuery, unifiedChunksSortedByRrfScore);

                rerankedChunks.RemoveAll(chunk => chunk.RelevanceScore < RelevanceFloor);

                if (rerankedChunks.Count == 0)
                {
                    _logger.LogInformation(
                        "Query: {userQuery} | No chunks cleared the relevance floor ({RelevanceFloor}) - returning empty context",
                        userQuery, RelevanceFloor);

                    await _fileDebugLogger.LogSectionAsync("3-rag-final-context", $"Query: {userQuery}",
                        "No chunks cleared the relevance floor - returning empty context.");

                    return new RetrievalResult { ChunkFallbacks = [], FullFileContents = [] };
                }

                var filesEligibleForExpansion = await _fileExpansionService.DetermineFilesEligibleForExpansionAsync(rerankedChunks, routedLlmQueryResponse);

                var filteredRetrievedChunksForPersistance = ChunkFiltering.CapChunksPerFile(rerankedChunks, routedLlmQueryResponse);

                var filteredRetrievedChunksForContext = ChunkFiltering.ExcludeChunksCoveredByExpandedFiles(filteredRetrievedChunksForPersistance, filesEligibleForExpansion);

                LogRedisSearchResults(filteredRetrievedChunksForContext, filesEligibleForExpansion, userQuery);

                var retrievalResult = new RetrievalResult { ChunkFallbacks = filteredRetrievedChunksForContext, FullFileContents = filesEligibleForExpansion };

                await _conversationReuseService.TryPersistReusableConversationArtifactsAsync(
                    conversationId, filteredRetrievedChunksForPersistance, retrievalResult.FullFileContents);

                await _fileDebugLogger.LogSectionAsync("3-rag-final-context", $"Query: {userQuery}",
                   RetrievalResultLogFormatter.FormatRetrievalResultIntoFileLog(retrievalResult));

                return retrievalResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure occurred while retrieving context for question: {UserQuery}", userQuery);
                return new RetrievalResult { ChunkFallbacks = [], FullFileContents = [] };
            }
        }

        private void LogRedisSearchResults
        (
            List<RetrievedChunk> filteredRetrievedChunks,
            Dictionary<string, string> filesEligibleForExpansion,
            string userQuery
        )
        {
            _logger.LogInformation("Query: {userQuery}", userQuery);

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
