using Microsoft.Extensions.Logging;
using TradingApp.Business.DTOs.ConversationChunk;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Infrastructure.Enums;
using TradingApp.Infrastructure.Helpers;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Services
{
    public class ChunkRetrievalService : IChunkRetrievalService
    {
        private readonly ILogger<ChunkRetrievalService> _logger;
        private readonly IQueryRoutingService _queryRoutingService;
        private readonly IKnowledgeBaseQueryService _knowledgeBaseQueryService;
        private readonly IChunkRerankingService _chunkRerankingService;
        private readonly IFileDebugLogger _fileDebugLogger;
        private readonly IFileExpansionService _fileExpansionService;
        private readonly IConversationChunkService _conversationChunkService;

        private const double RelevanceFloor = 0.53;

        public ChunkRetrievalService
        (
            ILogger<ChunkRetrievalService> logger,
            IQueryRoutingService queryRoutingService,
            IKnowledgeBaseQueryService knowledgeBaseQueryService,
            IChunkRerankingService chunkRerankingService,
            IFileDebugLogger fileDebugLogger,
            IFileExpansionService fileExpansionService,
            IConversationChunkService conversationChunkService)
        {
            _logger = logger;
            _queryRoutingService = queryRoutingService;
            _knowledgeBaseQueryService = knowledgeBaseQueryService;
            _chunkRerankingService = chunkRerankingService;
            _fileDebugLogger = fileDebugLogger;
            _fileExpansionService = fileExpansionService;
            _conversationChunkService = conversationChunkService;
        }

        public async Task<RetrievalResult> RetrieveRelevantContextAsync(string userQuestion, Guid conversationId)
        {
            try
            {
                var routedLlmQueryResponse = await _queryRoutingService.LlmQueryRouteAsync(userQuestion);

                var retrievedKNNChunks = await _knowledgeBaseQueryService.SearchKnnChunksAsync(userQuestion);

                var retrievedLexicalChunks = await _knowledgeBaseQueryService.SearchLexicalChunksAsync(userQuestion);

                var unifiedChunks = ChunkFusion.UnifyChunksFromBothSearchQueries(retrievedKNNChunks, retrievedLexicalChunks);

                var (knnChunksRankMap, lexicalChunksRankMap) = ChunkFusion.ComputeChunksRankMaps(retrievedKNNChunks, retrievedLexicalChunks);

                var unifiedChunksSortedByRrfScore = ChunkFusion.SortUnifiedChunksByRrfScore(unifiedChunks, knnChunksRankMap, lexicalChunksRankMap);

                var rerankedChunks = await _chunkRerankingService.RerankRetrievedChunksAsync(userQuestion, unifiedChunksSortedByRrfScore);

                rerankedChunks.RemoveAll(chunk => chunk.RelevanceScore < RelevanceFloor);

                if (rerankedChunks.Count == 0)
                {
                    _logger.LogInformation(
                        "Query: {userQuestion} | No chunks cleared the relevance floor ({RelevanceFloor}) - returning empty context",
                        userQuestion, RelevanceFloor);

                    await _fileDebugLogger.LogSectionAsync("rag-retrieval-after-filtering", $"Query: {userQuestion}",
                        "No chunks cleared the relevance floor - returning empty context.");

                    return new RetrievalResult { ChunkFallbacks = [], FullFileContents = [] };
                }

                var filesEligibleForExpansion = await _fileExpansionService.DetermineFilesEligibleForExpansionAsync(rerankedChunks, routedLlmQueryResponse);

                rerankedChunks.RemoveAll(chunk => filesEligibleForExpansion.Keys.Contains(chunk.SourceFile ?? string.Empty));

                var filteredRetrievedChunks = rerankedChunks
                     .GroupBy(x => x.SourceFile ?? string.Empty)
                     .SelectMany(group => group
                     .Take(MaxChunksPerFIle(routedLlmQueryResponse)))
                     .ToList();

                LogRedisSearchResults(filteredRetrievedChunks, filesEligibleForExpansion, userQuestion);

                var retrievalResult = new RetrievalResult { ChunkFallbacks = filteredRetrievedChunks, FullFileContents = filesEligibleForExpansion };

                await _conversationChunkService.CreateConversationChunkAsync(
                    RePackFilteredRetrievedChunks(retrievalResult.ChunkFallbacks, conversationId));

                await _fileDebugLogger.LogSectionAsync("rag-retrieval-after-filtering", $"Query: {userQuestion}",
                   RetrievalResultLogFormatter.FormatRetrievalResultIntoFileLog(retrievalResult));

                return retrievalResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure occurred while retrieving context for question: {UserQuestion}", userQuestion);
                return new RetrievalResult { ChunkFallbacks = [], FullFileContents = [] };
            }
        }
        //TODO: move this to a static helper service that has no dependencies or leave it here?
        private static List<CreateConversationChunkRequestDTO> RePackFilteredRetrievedChunks(List<RetrievedChunk> chunkFallbacks, Guid conversationId)
        {
            if (chunkFallbacks.Count == 0) return [];

            return chunkFallbacks.Select(x => new CreateConversationChunkRequestDTO
            {
                ConversationId = conversationId,
                Key = x.Key,
                SourceFile = x.SourceFile,
                Content = x.Content
            }).ToList();
        }

        private void LogRedisSearchResults(List<RetrievedChunk> filteredRetrievedChunks, Dictionary<string, string> filesEligibleForExpansion, string userQuestion)
        {
            _logger.LogInformation("Query: {userQuestion}", userQuestion);

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

        private static int MaxChunksPerFIle(LlmQueryClassification routedLlmQueryResponse)
        {
            switch (routedLlmQueryResponse)
            {
                case LlmQueryClassification.NARROW:
                    return 3;
                case LlmQueryClassification.BROAD:
                    return 5;
                case LlmQueryClassification.INCONCLUSIVE:
                    return 3;
                default:
                    return 3;
            }
        }
    }
}
