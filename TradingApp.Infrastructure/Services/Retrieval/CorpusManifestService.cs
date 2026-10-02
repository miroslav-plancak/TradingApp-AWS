using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using StackExchange.Redis;
using TradingApp.Infrastructure.Helpers;
using TradingApp.Infrastructure.Interfaces.Retrieval;
using TradingApp.Infrastructure.Models.Ingestion;

namespace TradingApp.Infrastructure.Services.Retrieval
{
    public class CorpusManifestService : ICorpusManifestService
    {
        private readonly ILogger<CorpusManifestService> _logger;
        private readonly IDatabase _database;
        private readonly IConnectionMultiplexer _connectionMultiplexer;
        private readonly IAsyncPolicy _resiliencePolicy;

        public CorpusManifestService
        (
            IConnectionMultiplexer connectionMultiplexer,
            ILogger<CorpusManifestService> logger,
            [FromKeyedServices(ResiliencePolicyKey.RedisAPI)] IAsyncPolicy resiliencePolicy)
        {
            _connectionMultiplexer = connectionMultiplexer;
            _database = _connectionMultiplexer.GetDatabase();
            _logger = logger;
            _resiliencePolicy = resiliencePolicy;
        }

        public async Task<List<ProcessedCorpusSourceFile>> GetEntireCorpusManifestAsync()
        {
            try
            {
                var manifest = await _resiliencePolicy.ExecuteAsync(async () =>
                {
                    var sourceFileNames = await _database.SetMembersAsync("corpus:sourcefiles");
                    var entries = new List<ProcessedCorpusSourceFile>();

                    foreach (var sourceFileName in sourceFileNames)
                    {
                        var fileName = (string?)sourceFileName ?? string.Empty;

                        var values = await _database.HashGetAsync($"corpus:file:{fileName}",
                            [new RedisValue("description"), new RedisValue("isfullyindexed")]);

                        var description = (string?)values[0] ?? string.Empty;
                        var isFullyIndexed = !values[1].IsNull && (bool)values[1];

                        entries.Add(new ProcessedCorpusSourceFile
                        {
                            Name = fileName,
                            Description = description,
                            IsFullyIndexed = isFullyIndexed
                        });
                    }

                    return entries;
                });

                return manifest;
            }
            catch (Exception ex) when (ResiliencePolicyBuilder.IsTransientRedisApiException(ex) || ex is BrokenCircuitException)
            {
                _logger.LogWarning(ex, "Redis database exception for fetching the corpus manifest.");
                return [];
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching the corpus manifest.");
                return [];
            }
        }
    }
}
