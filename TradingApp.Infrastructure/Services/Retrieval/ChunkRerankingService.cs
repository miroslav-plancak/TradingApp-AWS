using Microsoft.Extensions.Logging;
using TradingApp.Infrastructure.Helpers;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Models;
using TradingApp.Infrastructure.Helpers.Retrieval;
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
            await _fileDebugLogger.LogSectionAsync("1-rag-candidates", $"Query: {userMessage}",
                   RetrievalResultLogFormatter.FormatRetrievalResultIntoFileLog(new RetrievalResult { ChunkFallbacks = retrievedChunks }));

            try
            {

                var rerankResults = await _voyageRerankService.RerankAsync(userMessage, retrievedChunks.Select(x => x.Content ?? string.Empty).ToList());
                var rerankedChunks = rerankResults
                    .OrderByDescending(r => r.RelevanceScore)
                    .Select(x =>
                    {
                        var retrievedChunk = retrievedChunks[x.Index];
                        retrievedChunk.RelevanceScore = x.RelevanceScore;
                        return retrievedChunk;
                    })
                    .ToList();

                await _fileDebugLogger.LogSectionAsync("2-rag-reranked", $"Query: {userMessage}",
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
