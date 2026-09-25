using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using TradingApp.Infrastructure.Enums;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.ConversationMemory;
using TradingApp.Infrastructure.Interfaces.Retrieval;
using TradingApp.Infrastructure.Models.ConversationMemory;
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
        private readonly IQueryDecompositionService _queryDecompositionService;

        private const double RelevanceFloor = 0.53;

        public ChunkRetrievalService
        (
            ILogger<ChunkRetrievalService> logger,
            IQueryRoutingService queryRoutingService,
            IKnowledgeBaseQueryService knowledgeBaseQueryService,
            IChunkRerankingService chunkRerankingService,
            IFileDebugLogger fileDebugLogger,
            IFileExpansionService fileExpansionService,
            IConversationReuseService conversationReuseService,
            IQueryDecompositionService queryDecompositionService
        )
        {
            _logger = logger;
            _queryRoutingService = queryRoutingService;
            _knowledgeBaseQueryService = knowledgeBaseQueryService;
            _chunkRerankingService = chunkRerankingService;
            _fileDebugLogger = fileDebugLogger;
            _fileExpansionService = fileExpansionService;
            _conversationReuseService = conversationReuseService;
            _queryDecompositionService = queryDecompositionService;
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
                    _logger.LogInformation(
                        "Query: {userMessage} | Existing conversation chunk pool judged sufficient - skipping RAG pipeline",
                        userMessage);

                    var reusableRetrievalResult = BuildReorderedReusableRetrievalResult(reusableConversationContent);

                    await _fileDebugLogger.LogSectionAsync("3-arbiter-skip-context", $"Query: {userMessage}",
                        RetrievalResultLogFormatter.FormatRetrievalResultIntoFileLog(reusableRetrievalResult));
                    
                    return reusableRetrievalResult;
                }

                var routedLlmQueryResponse = await _queryRoutingService.LlmQueryRouteAsync(userMessage);

                var decomposedQueries = await _queryDecompositionService.DecomposeQueryAsync(userMessage);

                var combinedCappedChunks = await BuildCombinedCappedChunksAsync(decomposedQueries, routedLlmQueryResponse);

                var dedupedCombinedCappedChunks = DedupCombinedCappedChunks(combinedCappedChunks);

                if (dedupedCombinedCappedChunks.Count == 0)
                {
                    _logger.LogInformation(
                        "None of the decomposed queries: {decomposedQueries} | have chunks that cleared the relevance floor ({RelevanceFloor}) - returning empty context",
                        string.Join(", ", decomposedQueries), RelevanceFloor);

                    await _fileDebugLogger.LogSectionAsync("3-rag-final-context", $"Query: {userMessage}",
                        "None of the decomposed queries chunks cleared the relevance floor - returning empty context.");

                    return new RetrievalResult { ChunkFallbacks = [], FullFileContents = [] };
                }

                var filesEligibleForExpansion = await _fileExpansionService.DetermineFilesEligibleForExpansionAsync(
                    dedupedCombinedCappedChunks, routedLlmQueryResponse, decomposedQueries.Count);

                var filteredRetrievedChunksForContext = ChunkFiltering.ExcludeChunksCoveredByExpandedFiles(dedupedCombinedCappedChunks, filesEligibleForExpansion);

                LogRedisSearchResults(filteredRetrievedChunksForContext, filesEligibleForExpansion, userMessage);

                var retrievalResult = new RetrievalResult 
                { 
                    ChunkFallbacks = ChunkReordering.ReorderChunksToUShape(filteredRetrievedChunksForContext), 
                    FullFileContents = ChunkReordering.ReorderFullFilesToUShape(filesEligibleForExpansion, dedupedCombinedCappedChunks)
                };
                
                await _conversationReuseService.TryPersistReusableConversationArtifactsAsync(
                    conversationId, dedupedCombinedCappedChunks, retrievalResult.FullFileContents);

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

        private async Task<List<RetrievedChunk>> BuildCombinedCappedChunksAsync(List<string> decomposedQueries, LlmQueryClassification routedLlmQueryResponse) 
        {
            List<RetrievedChunk> combinedCappedChunks = [];

            foreach (var query in decomposedQueries)
            {
                var retrievedKNNChunks = await _knowledgeBaseQueryService.SearchKnnChunksAsync(query);

                var retrievedLexicalChunks = await _knowledgeBaseQueryService.SearchLexicalChunksAsync(query);

                var unifiedChunks = ChunkFusion.UnifyChunksFromBothSearchQueries(retrievedKNNChunks, retrievedLexicalChunks);

                var (knnChunksRankMap, lexicalChunksRankMap) = ChunkFusion.ComputeChunksRankMaps(retrievedKNNChunks, retrievedLexicalChunks);

                var  unifiedChunksSortedByRrfScore = ChunkFusion.SortUnifiedChunksByRrfScore(unifiedChunks, knnChunksRankMap, lexicalChunksRankMap);

                var rerankedChunks = await _chunkRerankingService.RerankRetrievedChunksAsync(query, unifiedChunksSortedByRrfScore);

                rerankedChunks.RemoveAll(chunk => chunk.RelevanceScore < RelevanceFloor);

                var cappedChunks = ChunkFiltering.CapChunksPerFile(rerankedChunks, routedLlmQueryResponse);

                combinedCappedChunks.AddRange(cappedChunks);
            }

            return combinedCappedChunks;
        }


        public async Task<List<RetrievedChunk>> RetrieveRelevantChunksAsync(string query)
        {
            try
            {
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

                return distinctCappedChunks;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure occurred while retrieving chunks for query: {Query}", query);
                return [];
            }
        }

        private List<RetrievedChunk> DedupCombinedCappedChunks(List<RetrievedChunk> combinedCappedChunks) 
        {
            return combinedCappedChunks
                .GroupBy(x => x.Key)
                .Select(group => group.OrderByDescending(x => x.RelevanceScore)
                .First())
                .ToList();
        }

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
