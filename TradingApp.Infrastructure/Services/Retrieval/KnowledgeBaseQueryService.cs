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

        public async Task<List<RetrievedChunk>> SearchKnnChunksAsync(string userMessage, CancellationToken ct)
        {
            try
            {
                var queryBytes = await EmbedQuestionAsync(userMessage, ct);

                ct.ThrowIfCancellationRequested();

                var searchResult = await _resiliencePolicy.ExecuteAsync(async (policyCancellationToken) =>
                {
                    return await _database.ExecuteAsync(
                     "FT.SEARCH", "idx:chunks",
                     "*=>[KNN 10 @embedding $BLOB AS score]",
                     "PARAMS", "2", "BLOB", queryBytes,
                     "SORTBY", "score",
                     "DIALECT", "2",
                     "RETURN", "5", "chunkIndex", "totalChunkCount", "sourceFile", "content", "score");
                }, ct);

                var retrievedKNNChunks = RedisResultParser.MapKnnSearchResultToRetrievedChunkList(searchResult);

                return retrievedKNNChunks;
            }
            catch (OperationCanceledException ocEx) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation(ocEx, "SearchKnnChunksCancelled | UserMessage: {UserMessage}", userMessage);
                throw;
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

        private async Task<byte[]> EmbedQuestionAsync(string userMessage, CancellationToken ct)
        {
            var queryEmbedding = await _voyageEmbeddingService.EmbedAsync(userMessage, ct);
            var queryBytes = EmbeddingPacker.RePackEmbeddingFromFloatToByte(queryEmbedding);

            return queryBytes;
        }

        public async Task<List<RetrievedChunk>> SearchLexicalChunksAsync(string userMessage, CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                var parsedUserMessage = RedisResultParser.ParseUserMessage(userMessage);

                if (parsedUserMessage.Length == 0)
                {
                    _logger.LogInformation("Lexical search skipped for userMessage: {UserMessage}", userMessage);
                    return new List<RetrievedChunk>();
                }

                var lexicalSearchResult = await _resiliencePolicy.ExecuteAsync(async (policyCancellationToken) =>
                {
                    return await _database.ExecuteAsync(
                                           "FT.SEARCH", "idx:chunks",
                                           $"@content:({parsedUserMessage})",
                                           "SCORER", "BM25",
                                           "WITHSCORES",
                                           "RETURN", "4", "chunkIndex", "totalChunkCount", "sourceFile", "content",
                                           "LIMIT", "0", "10",
                                           "DIALECT", "2");
                },ct);

                var retrievedLexicalChunks = RedisResultParser.MapLexicalSearchResultToRetrievedChunkList(lexicalSearchResult);

                return retrievedLexicalChunks;
            }
            catch (OperationCanceledException ocEx) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation(ocEx, "SearchLexicalChunksCancelled | UserMessage: {UserMessage}", userMessage);
                throw;
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

        public async Task<Dictionary<string, string>> GetSourceFileContentsAsync(IEnumerable<string> sourceFiles, CancellationToken ct)
        {
            var distinctFiles = sourceFiles.Distinct().ToList();

            var fetchTasks = distinctFiles.Select(async sourceFile =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    var content = await _resiliencePolicy.ExecuteAsync(async (policyCancellationToken) =>
                    {
                        return await _database.HashGetAsync($"file:{sourceFile}", "content");
                    }, ct);

                    return (sourceFile, content: (string?)content ?? string.Empty);
                }
                catch (OperationCanceledException ocEx) when (ct.IsCancellationRequested)
                {
                    _logger.LogInformation(ocEx, "GetSourceFileContentsCancelled | SourceFile: {SourceFile}", sourceFile);
                    throw;
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
