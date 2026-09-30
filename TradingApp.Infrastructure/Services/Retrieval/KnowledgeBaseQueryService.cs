using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using StackExchange.Redis;
using TradingApp.Infrastructure.Helpers;
using TradingApp.Infrastructure.Helpers.Ingestion;
using TradingApp.Infrastructure.Helpers.Retrieval;
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
                     "RETURN", "5", "chunkIndex","totalChunkCount", "sourceFile", "content", "score");
                });

                var retrievedKNNChunks = RedisResultParser.MapKnnSearchResultToRetrievedChunkList(searchResult);

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

        public async Task<List<RetrievedChunk>> SearchLexicalChunksAsync(string userMessage)
        {
            try
            {
                var parsedUserMessage = RedisResultParser.ParseUserMessage(userMessage);

                if (parsedUserMessage.Length == 0)
                {
                    _logger.LogInformation("Lexical search skipped for userMessage: {UserMessage}", userMessage);
                    return new List<RetrievedChunk>();
                }

                var lexicalSearchResult = await _resiliencePolicy.ExecuteAsync(async () =>
                {
                    return await _database.ExecuteAsync(
                                           "FT.SEARCH", "idx:chunks",
                                           $"@content:({parsedUserMessage})",
                                           "SCORER", "BM25",
                                           "WITHSCORES",
                                           "RETURN", "4", "chunkIndex", "totalChunkCount", "sourceFile", "content",
                                           "LIMIT", "0", "10",
                                           "DIALECT", "2");
                });

                var retrievedLexicalChunks = RedisResultParser.MapLexicalSearchResultToRetrievedChunkList(lexicalSearchResult);

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
