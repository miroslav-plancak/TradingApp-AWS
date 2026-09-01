using Microsoft.Extensions.Logging;
using TradingApp.Infrastructure.Helpers;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Services
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

        public async Task<List<RetrievedChunk>> RerankRetrievedChunksAsync(string userQuestion, List<RetrievedChunk> retrievedChunks)
        {
            await _fileDebugLogger.LogSectionAsync("rag-retrieval-pre-reranking", $"Query: {userQuestion}",
                   RetrievalResultLogFormatter.FormatRetrievalResultIntoFileLog(new RetrievalResult { ChunkFallbacks = retrievedChunks }));

            try
            {

                var rerankResults = await _voyageRerankService.RerankAsync(userQuestion, retrievedChunks.Select(x => x.Content ?? string.Empty).ToList());
                var rerankedChunks = rerankResults
                    .OrderByDescending(r => r.RelevanceScore)
                    .Select(x =>
                    {
                        var retrievedChunk = retrievedChunks[x.Index];
                        retrievedChunk.RelevanceScore = x.RelevanceScore;
                        return retrievedChunk;
                    })
                    .ToList();

                await _fileDebugLogger.LogSectionAsync("rag-retrieval-post-reranking", $"Query: {userQuestion}",
                  RetrievalResultLogFormatter.FormatRerankResultIntoFileLog(rerankResults.ToList()));

                return rerankedChunks;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reranking failed for user question: {UserQuestion}", userQuestion);
                return new List<RetrievedChunk>();
            }
        }
    }
}
