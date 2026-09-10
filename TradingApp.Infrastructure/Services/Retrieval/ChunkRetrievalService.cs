using Microsoft.Extensions.Logging;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces;
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
            string userMessage,
            Guid conversationId
        )
        {
            try
            {
                var reusableConversationContent = await _conversationReuseService.TryRetrieveReusableConversationArtifactsAsync(conversationId, userMessage);

                if (reusableConversationContent.ConversationChunks.Count > 0)
                {
                    var reusableRetrievalResult = RetrievalResultMapping.ToRetrievalResult(reusableConversationContent);

                    _logger.LogInformation(
                        "Query: {userMessage} | Existing conversation chunk pool judged sufficient - skipping RAG pipeline",
                        userMessage);

                    await _fileDebugLogger.LogSectionAsync("3-arbiter-skip-context", $"Query: {userMessage}",
                        RetrievalResultLogFormatter.FormatRetrievalResultIntoFileLog(reusableRetrievalResult));

                    return reusableRetrievalResult;
                }

                var routedLlmQueryResponse = await _queryRoutingService.LlmQueryRouteAsync(userMessage);

                var retrievedKNNChunks = await _knowledgeBaseQueryService.SearchKnnChunksAsync(userMessage);

                var retrievedLexicalChunks = await _knowledgeBaseQueryService.SearchLexicalChunksAsync(userMessage);

                var unifiedChunks = ChunkFusion.UnifyChunksFromBothSearchQueries(retrievedKNNChunks, retrievedLexicalChunks);

                var (knnChunksRankMap, lexicalChunksRankMap) = ChunkFusion.ComputeChunksRankMaps(retrievedKNNChunks, retrievedLexicalChunks);

                var unifiedChunksSortedByRrfScore = ChunkFusion.SortUnifiedChunksByRrfScore(unifiedChunks, knnChunksRankMap, lexicalChunksRankMap);

                var rerankedChunks = await _chunkRerankingService.RerankRetrievedChunksAsync(userMessage, unifiedChunksSortedByRrfScore);

                rerankedChunks.RemoveAll(chunk => chunk.RelevanceScore < RelevanceFloor);

                if (rerankedChunks.Count == 0)
                {
                    _logger.LogInformation(
                        "Query: {userMessage} | No chunks cleared the relevance floor ({RelevanceFloor}) - returning empty context",
                        userMessage, RelevanceFloor);

                    await _fileDebugLogger.LogSectionAsync("3-rag-final-context", $"Query: {userMessage}",
                        "No chunks cleared the relevance floor - returning empty context.");

                    return new RetrievalResult { ChunkFallbacks = [], FullFileContents = [] };
                }

                var filesEligibleForExpansion = await _fileExpansionService.DetermineFilesEligibleForExpansionAsync(rerankedChunks, routedLlmQueryResponse);

                var filteredRetrievedChunksForPersistance = ChunkFiltering.CapChunksPerFile(rerankedChunks, routedLlmQueryResponse);

                var filteredRetrievedChunksForContext = ChunkFiltering.ExcludeChunksCoveredByExpandedFiles(filteredRetrievedChunksForPersistance, filesEligibleForExpansion);

                LogRedisSearchResults(filteredRetrievedChunksForContext, filesEligibleForExpansion, userMessage);

                var retrievalResult = new RetrievalResult { ChunkFallbacks = filteredRetrievedChunksForContext, FullFileContents = filesEligibleForExpansion };

                await _conversationReuseService.TryPersistReusableConversationArtifactsAsync(
                    conversationId, filteredRetrievedChunksForPersistance, retrievalResult.FullFileContents);

                await _fileDebugLogger.LogSectionAsync("3-rag-final-context", $"Query: {userMessage}",
                   RetrievalResultLogFormatter.FormatRetrievalResultIntoFileLog(retrievalResult));

                return retrievalResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure occurred while retrieving context for question: {UserMessage}", userMessage);
                return new RetrievalResult { ChunkFallbacks = [], FullFileContents = [] };
            }
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
