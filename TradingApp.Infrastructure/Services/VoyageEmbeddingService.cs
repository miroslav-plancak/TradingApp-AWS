using Amazon.Runtime.Internal.Util;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using TradingApp.Infrastructure.Interfaces;

namespace TradingApp.Infrastructure.Services
{
    public class VoyageEmbeddingService : IVoyageEmbeddingService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<VoyageEmbeddingService> _logger;

        private const string EmbeddingModel = "voyage-4-lite";

        public VoyageEmbeddingService(HttpClient httpClient, ILogger<VoyageEmbeddingService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            var results = await EmbedBatchAsync(new[] { text }, cancellationToken);
            return results[0];
        }

        public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            var request = new VoyageEmbedingRequest
            {
                Input = texts,
                Model = EmbeddingModel
            };

            try
            {
                var response = await _httpClient.PostAsJsonAsync("embeddings", request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var payload = await response.Content.ReadFromJsonAsync<VoyageEmbeddingResponse>(cancellationToken: cancellationToken)
                    ?? throw new InvalidOperationException("Voyage API returned an empty response.");

                return payload.Data
                    .OrderBy(pd => pd.Index)
                    .Select(pd => pd.Embedding)
                    .ToList();
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, "Voyage embeddings request failed | ChunkCount: {ChunkCount}", texts.Count);
                throw;
            }
        }

        private class VoyageEmbedingRequest
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
        public class VoyageEmbeddingData
        {
            [JsonPropertyName("embedding")]
            public required float[] Embedding { get; set; }

            [JsonPropertyName("index")]
            public int Index { get; set; }
        }
    }


}
