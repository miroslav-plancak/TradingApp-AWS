using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Threading.Tasks;
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
        // Empirically observed 2026-08-21: off-topic questions scored 0.756-0.786 on their
        // best chunk, genuinely relevant questions scored 0.43-0.6. Not yet validated against
        // a larger sample - see task #40. Treat as a hypothesis, not a settled number.
        private const double RelevanceCutoff = 0.76;
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

        public async Task<RetrievalResult> RetrieveRelevantChunksAsync(string userQuestion)
        {
            var queryBytes = await EmbedQuestionAsync(userQuestion);

            var searchResult = await _database.ExecuteAsync(
             "FT.SEARCH", "idx:chunks",
             "*=>[KNN 10 @embedding $BLOB AS score]",
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

            retrievedChunks.RemoveAll(chunk => double.TryParse( chunk.Score, out double result) && result >= RelevanceCutoff);

            var fileOccurrenceMap = retrievedChunks.GroupBy(x => x.SourceFile ?? string.Empty).ToDictionary(g => g.Key, g => g.Count());

            var distinctFileNames = retrievedChunks.DistinctBy(x => x.SourceFile).Select(x => x.SourceFile);
            var filesWithFullContent = await GetExistingFullFileContentsMapAsync(distinctFileNames);

            var filesEligibleForExpansion = filesWithFullContent
                        .Where(kvp => fileOccurrenceMap[kvp.Key] >= 2)
                        .ToDictionary(x => x.Key, x => x.Value);

            retrievedChunks.RemoveAll(chunk => filesEligibleForExpansion.Keys.Contains(chunk.SourceFile ?? string.Empty));

            var filteredRetrievedChunks = retrievedChunks
                 .GroupBy(x => x.SourceFile ?? string.Empty)
                 .SelectMany(group => group
                 .OrderBy(x => x.Score)
                 .Take(3))
                 .ToList();

            var totalMatches = searchResult[0];
            LogRedisSearchResults(retrievedChunks, userQuestion, totalMatches);

            return new RetrievalResult { ChunkFallbacks = filteredRetrievedChunks, FullFileContents = filesEligibleForExpansion };

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

        private async Task<Dictionary<string, string>> GetExistingFullFileContentsMapAsync(IEnumerable<string?> distinctFileNames)
        {
            var allFetchedFileContents = await GetSourceFileContentsAsync(distinctFileNames.OfType<string>());
            var filesWithContent = allFetchedFileContents.Where(x => x.Value != string.Empty).ToDictionary(x => x.Key, x => x.Value);
            return filesWithContent;
        }

        private async Task<Dictionary<string, string>> GetSourceFileContentsAsync(IEnumerable<string> sourceFiles)
        {
            var distinctFiles = sourceFiles.Distinct().ToList();

            var fetchTasks = distinctFiles.Select(async sourceFile =>
            {
                var content = await _database.HashGetAsync($"file:{sourceFile}", "content");
                return (sourceFile, content);
            });

            var results = await Task.WhenAll(fetchTasks);

            return results.ToDictionary(r => r.sourceFile, r => (string?)r.content ?? string.Empty);
        }
    }
}
