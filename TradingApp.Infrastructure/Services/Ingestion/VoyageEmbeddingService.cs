using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.Ingestion;

namespace TradingApp.Infrastructure.Services.Ingestion
{
    public class VoyageEmbeddingService : IVoyageEmbeddingService
    {
        private readonly ILogger<VoyageEmbeddingService> _logger;
        private readonly IVoyageApiService _voyageApiService;

        private const string EmbeddingModel = "voyage-4-lite";

        public VoyageEmbeddingService
        (
            ILogger<VoyageEmbeddingService> logger,
            IVoyageApiService voyageApiService)
        {
            _logger = logger;
            _voyageApiService = voyageApiService;
        }

        public async Task<float[]> EmbedAsync
        (
            string text,
            CancellationToken cancellationToken = default
        )
        {
            var results = await EmbedBatchAsync(new[] { text }, cancellationToken);
            return results[0];
        }

        public async Task<IReadOnlyList<float[]>> EmbedBatchAsync
        (
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default
        )
        {
            var request = new VoyageEmbeddingRequest
            {
                Input = texts,
                Model = EmbeddingModel
            };

            try
            {
                var response = await _voyageApiService.DispatchRequestAsync<VoyageEmbeddingRequest, VoyageEmbeddingResponse>(
                    "embeddings", request, cancellationToken);

                return response.Data
                    .OrderBy(pd => pd.Index)
                    .Select(pd => pd.Embedding)
                    .ToList();
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, "Voyage embeddings request failed | ChunkCount: {ChunkCount}", texts.Count);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failture occurred while dispatching Voyage embeddings request | ChunkCount: {ChunkCount}",
                    texts.Count);
                throw;
            }
        }

        private class VoyageEmbeddingRequest
        {
            [JsonPropertyName("input")]
            public required IReadOnlyList<string> Input { get; set; }

            [JsonPropertyName("model")]
            public required string Model { get; set; }
        }

        private class VoyageEmbeddingResponse
        {
            [JsonPropertyName("data")]
            public required List<VoyageEmbeddingData> Data { get; set; }
        }

        private class VoyageEmbeddingData
        {
            [JsonPropertyName("embedding")]
            public required float[] Embedding { get; set; }

            [JsonPropertyName("index")]
            public int Index { get; set; }
        }
    }
}
