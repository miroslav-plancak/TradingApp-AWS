using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TradingApp.Infrastructure.Helpers;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Services
{   
    public class ChunkRetrievalService : IChunkRetrievalService
    {
        private readonly IVoyageEmbeddingService _voyageEmbeddingService;
        private readonly IConnectionMultiplexer _connectionMultiplexer;
        private readonly ILogger<ChunkRetrievalService> _logger;
        private readonly IDatabase _database;

        public ChunkRetrievalService
        (
            IVoyageEmbeddingService voyageEmbeddingService,
            IConnectionMultiplexer connectionMultiplexer,
            ILogger<ChunkRetrievalService> logger
        )
        {
            _voyageEmbeddingService = voyageEmbeddingService;
            _connectionMultiplexer = connectionMultiplexer;
            _logger = logger;
            _database = connectionMultiplexer.GetDatabase();
        }

        public async Task<List<RetrievedChunk>> RetrieveRelevantChunksAsync(string userQuestion)
        {
            var queryBytes = await EmbedQuestionAsync(userQuestion);

            var searchResult = await _database.ExecuteAsync(
             "FT.SEARCH", "idx:chunks",
             "*=>[KNN 3 @embedding $BLOB AS score]",
             "PARAMS", "2", "BLOB", queryBytes,
             "SORTBY", "score",
             "DIALECT", "2",
             "RETURN", "3", "sourceFile", "content", "score");

            var retrievedChunks = new List<RetrievedChunk>();

            for (var i = 1; i < searchResult.Length; i += 2)
            {
                var fields = searchResult[i + 1];
                var fieldMap = new Dictionary<string, string>();

                for (var f = 0; f < fields.Length; f += 2)
                {
                    fieldMap[(string)fields[f]!] = (string)fields[f + 1]!;
               
                }

                retrievedChunks.Add(new RetrievedChunk
                {
                    Score = fieldMap.TryGetValue("score", out var score) ? score : string.Empty,
                    SourceFile = fieldMap.TryGetValue("sourceFile", out var sourceFile) ? sourceFile : string.Empty,
                    Content = fieldMap.TryGetValue("content", out var content) ? content : string.Empty
                });

            }

            var totalMatches = searchResult[0];
            LogRedisSearchResults(retrievedChunks, userQuestion, totalMatches);

            return retrievedChunks;
        }

        private async Task<byte[]> EmbedQuestionAsync(string userQuestion)
        {
            var queryEmbedding = await _voyageEmbeddingService.EmbedAsync(userQuestion);
            var queryBytes = EmbeddingPacker.RePackEmbeddingFromFloatToByte(queryEmbedding);

            return queryBytes;
        }

        private void LogRedisSearchResults(List<RetrievedChunk> results, string userQuestion, RedisResult totalMatches)
        {
            _logger.LogInformation("Query: {userQuestion} | candidates {totalMatches}", userQuestion, totalMatches);

            foreach (var result in results)
            {
                _logger.LogInformation("SourceFile: {SourceFile} | score={Score}", result.SourceFile?.ToString(), result.Score?.ToString());
            }
        }       
    }
}
