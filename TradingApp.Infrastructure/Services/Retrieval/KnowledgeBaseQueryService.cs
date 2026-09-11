using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using StackExchange.Redis;
using System.Text.RegularExpressions;
using TradingApp.Infrastructure.Helpers;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Models;
using TradingApp.Infrastructure.Helpers.Ingestion;
using TradingApp.Infrastructure.Interfaces.Ingestion;
using TradingApp.Infrastructure.Interfaces.Retrieval;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Services.Retrieval
{
    public class KnowledgeBaseQueryService : IKnowledgeBaseQueryService
    {
        private readonly ILogger<KnowledgeBaseQueryService> _logger;
        private readonly IDatabase _database;
        private readonly IConnectionMultiplexer _connectionMultiplexer;
        private readonly IVoyageEmbeddingService _voyageEmbeddingService;
        private readonly IAsyncPolicy _resiliencePolicy;

        public KnowledgeBaseQueryService
        (
            IConnectionMultiplexer connectionMultiplexer,
            IVoyageEmbeddingService voyageEmbeddingService,
            ILogger<KnowledgeBaseQueryService> logger,
            [FromKeyedServices(ResiliencePolicyKey.RedisAPI)] IAsyncPolicy resiliencePolicy)
        {
            _connectionMultiplexer = connectionMultiplexer;
            _database = _connectionMultiplexer.GetDatabase();
            _voyageEmbeddingService = voyageEmbeddingService;
            _logger = logger;
            _resiliencePolicy = resiliencePolicy;
        }

        public async Task<List<RetrievedChunk>> SearchKnnChunksAsync(string userMessage)
        {
            try
            {
                var queryBytes = await EmbedQuestionAsync(userMessage);

                var searchResult = await _resiliencePolicy.ExecuteAsync(async () =>
                {
                    return await _database.ExecuteAsync(
                     "FT.SEARCH", "idx:chunks",
                     "*=>[KNN 10 @embedding $BLOB AS score]",
                     "PARAMS", "2", "BLOB", queryBytes,
                     "SORTBY", "score",
                     "DIALECT", "2",
                     "RETURN", "3", "sourceFile", "content", "score");
                });

                var retrievedKNNChunks = MapKnnSearchResultToRetrievedChunkList(searchResult);

                return retrievedKNNChunks;
            }
            catch (Exception ex) when (ResiliencePolicyBuilder.IsTransientRedisApiException(ex) || ex is BrokenCircuitException)
            {
                _logger.LogWarning(ex, "Redis database exception for user question: {UserMessage}", userMessage);
                return new List<RetrievedChunk>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database or embedding error for user question: {UserMessage}", userMessage);
                return new List<RetrievedChunk>();
            }

        }

        private async Task<byte[]> EmbedQuestionAsync(string userMessage)
        {
            var queryEmbedding = await _voyageEmbeddingService.EmbedAsync(userMessage);
            var queryBytes = EmbeddingPacker.RePackEmbeddingFromFloatToByte(queryEmbedding);

            return queryBytes;
        }

        private static List<RetrievedChunk> MapKnnSearchResultToRetrievedChunkList(RedisResult knnSearchResult)
        {
            var retrievedChunks = new List<RetrievedChunk>();

            for (var i = 1; i < knnSearchResult.Length; i += 2)
            {
                var key = (string?)knnSearchResult[i];
                var fieldMap = BuildFieldMap(knnSearchResult[i + 1]);

                retrievedChunks.Add(new RetrievedChunk
                {
                    Key = key,
                    KnnScore = fieldMap.TryGetValue("score", out var score) && double.TryParse(score, out var knnScore) ? knnScore : null,
                    SourceFile = fieldMap.TryGetValue("sourceFile", out var sourceFile) ? sourceFile : string.Empty,
                    Content = fieldMap.TryGetValue("content", out var content) ? content : string.Empty
                });
            }

            return retrievedChunks;
        }

        public async Task<List<RetrievedChunk>> SearchLexicalChunksAsync(string userMessage)
        {
            try
            {
                var parsedUserMessage = ParseUserMessage(userMessage);

                var lexicalSearchResult = await _resiliencePolicy.ExecuteAsync(async () =>
                {
                    return await _database.ExecuteAsync(
                                           "FT.SEARCH", "idx:chunks",
                                           $"@content:({parsedUserMessage})",
                                           "SCORER", "BM25",
                                           "WITHSCORES",
                                           "RETURN", "2", "sourceFile", "content",
                                           "LIMIT", "0", "10",
                                           "DIALECT", "2");
                });

                var retrievedLexicalChunks = MapLexicalSearchResultToRetrievedChunkList(lexicalSearchResult);

                return retrievedLexicalChunks;
            }
            catch (Exception ex) when (ResiliencePolicyBuilder.IsTransientRedisApiException(ex) || ex is BrokenCircuitException)
            {
                _logger.LogWarning(ex, "Redis database exception for user question: {UserMessage}", userMessage);
                return new List<RetrievedChunk>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database error for user question: {UserMessage}", userMessage);
                return new List<RetrievedChunk>();
            }
        }

        private static string ParseUserMessage(string userMessage)
        {
            var terms = Regex.Split(userMessage, @"[^\w]+")
               .Where(t => t.Length > 0)
               .Where(IsValidIdentifier)
               .ToList();

            return string.Join("|", terms);
        }

        private static bool IsValidIdentifier(string token)
        {
            var hasUnderscore = token.Contains('_');
            var hasInternalCaps = token.Skip(1).Any(char.IsUpper);
            var isAllCaps = token.Length > 1 && token.All(char.IsUpper);

            return hasUnderscore || hasInternalCaps || isAllCaps;
        }

        private static List<RetrievedChunk> MapLexicalSearchResultToRetrievedChunkList(RedisResult lexicalSearchResult)
        {
            var retrievedChunks = new List<RetrievedChunk>();

            for (var i = 1; i < lexicalSearchResult.Length; i += 3)
            {
                var key = (string?)lexicalSearchResult[i];
                var lexicalScore = (double)lexicalSearchResult[i + 1];
                var fieldMap = BuildFieldMap(lexicalSearchResult[i + 2]);

                retrievedChunks.Add(new RetrievedChunk
                {
                    Key = key,
                    LexicalScore = lexicalScore,
                    SourceFile = fieldMap.TryGetValue("sourceFile", out var sourceFile) ? sourceFile : string.Empty,
                    Content = fieldMap.TryGetValue("content", out var content) ? content : string.Empty
                });
            }

            return retrievedChunks;
        }

        private static Dictionary<string,string> BuildFieldMap(RedisResult searchResult)
        {
            var fieldMap = new Dictionary<string, string>();

            for (var f = 0; f < searchResult.Length; f += 2)
            {
                fieldMap[(string)searchResult[f]!] = (string)searchResult[f + 1]!;

            }

            return fieldMap;
        }

        public async Task<Dictionary<string, string>> GetSourceFileContentsAsync(IEnumerable<string> sourceFiles)
        {
            var distinctFiles = sourceFiles.Distinct().ToList();

            var fetchTasks = distinctFiles.Select(async sourceFile =>
            {
                try
                {
                    var content = await _resiliencePolicy.ExecuteAsync(async () =>
                    {
                        return await _database.HashGetAsync($"file:{sourceFile}", "content");
                    });

                    return (sourceFile, content: (string?)content ?? string.Empty);
                }
                catch (Exception ex) when (ResiliencePolicyBuilder.IsTransientRedisApiException(ex) || ex is BrokenCircuitException)
                {
                    _logger.LogWarning(ex, "Redis database exception for fetching full file content for source file: {SourceFile}", sourceFile);
                    return (sourceFile, content: string.Empty);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching full file content for source file: {SourceFile}", sourceFile);
                    return (sourceFile, content: string.Empty);
                }
            });

            var results = await Task.WhenAll(fetchTasks);

            return results.ToDictionary(r => r.sourceFile, r => r.content);
        }

    }
}
