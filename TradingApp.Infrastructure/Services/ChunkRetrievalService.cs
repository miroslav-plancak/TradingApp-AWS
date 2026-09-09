using Microsoft.Extensions.Logging;
using TradingApp.Business.DTOs.ConversationChunk;
using TradingApp.Business.DTOs.ConversationFullFile;
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
        private readonly IConversationFullFileService _conversationFullFileService;
        private readonly IConversationContextArbiterService _conversationContextArbiterService;

        private const double RelevanceFloor = 0.53;

        public ChunkRetrievalService
        (
            ILogger<ChunkRetrievalService> logger,
            IQueryRoutingService queryRoutingService,
            IKnowledgeBaseQueryService knowledgeBaseQueryService,
            IChunkRerankingService chunkRerankingService,
            IFileDebugLogger fileDebugLogger,
            IFileExpansionService fileExpansionService,
            IConversationChunkService conversationChunkService,
            IConversationFullFileService conversationFullFileService,
            IConversationContextArbiterService conversationContextArbiterService
          )
        {
            _logger = logger;
            _queryRoutingService = queryRoutingService;
            _knowledgeBaseQueryService = knowledgeBaseQueryService;
            _chunkRerankingService = chunkRerankingService;
            _fileDebugLogger = fileDebugLogger;
            _fileExpansionService = fileExpansionService;
            _conversationChunkService = conversationChunkService;
            _conversationFullFileService = conversationFullFileService;
            _conversationContextArbiterService = conversationContextArbiterService;
        }

        public async Task<RetrievalResult> RetrieveRelevantContextAsync(string userQuestion, Guid conversationId)
        {
            try
            {
                var allExistingConversationChunks = await _conversationChunkService.GetConversationChunksAsync(conversationId);

                var sufficientPoolChunks = await _conversationContextArbiterService.DetermineSufficientChunksAsync(userQuestion, allExistingConversationChunks);

                var sufficientPoolFullFiles = await _conversationFullFileService.GetSpecificConversationFullFilesAsync(sufficientPoolChunks);

                if (sufficientPoolChunks.Count > 0)
                {
                    var fullFileContents = MapToFullFileContents(sufficientPoolFullFiles);

                    var poolRetrievalResult = new RetrievalResult
                    { 
                        ChunkFallbacks = MapToRetrievedChunks(sufficientPoolChunks)
                            .Where(x => !fullFileContents.ContainsKey(x.SourceFile ?? string.Empty))
                            .ToList(), 
                        FullFileContents = fullFileContents
                    };

                    _logger.LogInformation(
                        "Query: {userQuestion} | Existing conversation chunk pool judged sufficient - skipping RAG pipeline",
                        userQuestion);

                    await _fileDebugLogger.LogSectionAsync("3-arbiter-skip-context", $"Query: {userQuestion}",
                        RetrievalResultLogFormatter.FormatRetrievalResultIntoFileLog(poolRetrievalResult));

                    return poolRetrievalResult;
                }

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

                    await _fileDebugLogger.LogSectionAsync("3-rag-final-context", $"Query: {userQuestion}",
                        "No chunks cleared the relevance floor - returning empty context.");

                    return new RetrievalResult { ChunkFallbacks = [], FullFileContents = [] };
                }

                var filesEligibleForExpansion = await _fileExpansionService.DetermineFilesEligibleForExpansionAsync(rerankedChunks, routedLlmQueryResponse);

                var filteredRetrievedChunksForPersistance = rerankedChunks
                     .GroupBy(x => x.SourceFile ?? string.Empty)
                     .SelectMany(group => group
                     .Take(MaxChunksPerFIle(routedLlmQueryResponse)))
                     .ToList();

                var filteredRetrievedChunksForContext = filteredRetrievedChunksForPersistance
                    .Where(x => !filesEligibleForExpansion.ContainsKey(x.SourceFile ?? string.Empty))
                    .ToList();

                LogRedisSearchResults(filteredRetrievedChunksForContext, filesEligibleForExpansion, userQuestion);

                var retrievalResult = new RetrievalResult { ChunkFallbacks = filteredRetrievedChunksForContext, FullFileContents = filesEligibleForExpansion };

                await _conversationChunkService.CreateConversationChunksAsync(
                    RePackFilteredRetrievedChunks(filteredRetrievedChunksForPersistance, conversationId));

                await _conversationFullFileService.CreateConversationFullFilesAsync(
                    RePackFilesEligibleForExpansion(retrievalResult.FullFileContents, conversationId));

                await _fileDebugLogger.LogSectionAsync("3-rag-final-context", $"Query: {userQuestion}",
                   RetrievalResultLogFormatter.FormatRetrievalResultIntoFileLog(retrievalResult));

                return retrievalResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure occurred while retrieving context for question: {UserQuestion}", userQuestion);
                return new RetrievalResult { ChunkFallbacks = [], FullFileContents = [] };
            }
        }

        private static List<RetrievedChunk> MapToRetrievedChunks(List<CreatedConversationChunkResponseDTO> chunks)
        {
            return chunks.Select(x => new RetrievedChunk
            {
                Key = x.Key,
                SourceFile = x.SourceFile,
                Content = x.Content,
                KnnScore = null,
                LexicalScore = null,
                RelevanceScore = 0,
                ReciprocalRankFusionScore = 0
            }).ToList();
        }

        private static Dictionary<string, string> MapToFullFileContents(List<CreatedConversationFullFileResponseDTO> fullFiles)
        {
            return fullFiles.ToDictionary(x => x.SourceFile, x => x.Content);
        }

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

        private List<CreateConversationFullFileRequestDTO> RePackFilesEligibleForExpansion(Dictionary<string, string> fullFileContents, Guid conversationId)
        {
            if (fullFileContents.Count == 0) return [];

            return fullFileContents.Select(x => new CreateConversationFullFileRequestDTO
            {
                ConversationId = conversationId,
                SourceFile = x.Key,
                Content = x.Value
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
