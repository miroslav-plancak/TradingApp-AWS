using Microsoft.Extensions.Logging;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.Retrieval;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Services.Retrieval
{
    public class ChunkRerankingService : IChunkRerankingService
    {
        private readonly ILogger<ChunkRerankingService> _logger;
        private readonly IVoyageRerankService _voyageRerankService;
        private readonly IFileDebugLogger _fileDebugLogger;

        public ChunkRerankingService
        (
            IFileDebugLogger fileDebugLogger,
            IVoyageRerankService voyageRerankService,
            ILogger<ChunkRerankingService> logger
        )
        {
            _fileDebugLogger = fileDebugLogger;
            _voyageRerankService = voyageRerankService;
            _logger = logger;
        }

        public async Task<List<RetrievedChunk>> RerankRetrievedChunksAsync
        (
            string userMessage,
            List<RetrievedChunk> retrievedChunks
        )
        {
            await _fileDebugLogger.LogSectionAsync("1-rag-candidates-pre-rerank", $"Query: {userMessage}",
                   RetrievalResultLogFormatter.FormatRetrievalResultIntoFileLog(new RetrievalResult { ChunkFallbacks = retrievedChunks }));

            try
            {

                var rerankResults = await _voyageRerankService.RerankAsync(userMessage, retrievedChunks.Select(x => x.Content ?? string.Empty).ToList());
                var rerankedChunks = rerankResults
                    .OrderByDescending(r => r.RelevanceScore)
                    .Select(x =>
                    {
                        var originalRetrievedChunk = retrievedChunks[x.Index];

                        return new RetrievedChunk
                        {
                            Key = originalRetrievedChunk.Key,
                            SourceFile = originalRetrievedChunk.SourceFile,
                            Content = originalRetrievedChunk.Content,
                            KnnScore = originalRetrievedChunk.KnnScore,
                            LexicalScore = originalRetrievedChunk.LexicalScore,
                            ReciprocalRankFusionScore = originalRetrievedChunk.ReciprocalRankFusionScore,
                            RelevanceScore = x.RelevanceScore
                        };
                    })
                    .ToList();

                await _fileDebugLogger.LogSectionAsync("2-rag-post-rerank", $"Query: {userMessage}",
                  RetrievalResultLogFormatter.FormatRerankResultIntoFileLog(rerankResults.ToList()));

                return rerankedChunks;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reranking failed for user question: {UserMessage}", userMessage);
                return new List<RetrievedChunk>();
            }
        }
    }
}
