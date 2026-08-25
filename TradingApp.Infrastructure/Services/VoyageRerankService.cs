using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Services
{
    public class VoyageRerankService : IVoyageRerankService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<VoyageRerankService> _logger;

        public VoyageRerankService(HttpClient httpClient, ILogger<VoyageRerankService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<IReadOnlyList<RerankResult>> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken ct = default)
        {
            var request = new VoyageRerankRequest
            {
                Query = query,
                Documents = documents.ToList(),
                Model = "rerank-2.5"

            };

            try
            {
                var response = await _httpClient.PostAsJsonAsync("rerank", request, ct);
                response.EnsureSuccessStatusCode();

                var payload = await response.Content.ReadFromJsonAsync<VoyageRerankResponse>(cancellationToken: ct)
                    ?? throw new InvalidOperationException("Voyage API returned an empty response.");

                return payload.Data
                    .Select(x => new RerankResult { Index = x.Index, RelevanceScore = x.RelevanceScore })
                    .ToList();
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, "Voyage rerank request failed | DocumentCount: {DocumentCount}", documents.Count);
                throw;
            }
        }


        private class VoyageRerankRequest
        {
            [JsonPropertyName("query")]
            public required string Query { get; set; }

            [JsonPropertyName("documents")]
            public required IReadOnlyList<string> Documents { get; set; }

            [JsonPropertyName("model")]
            public required string Model { get; set; }
        }

        private class VoyageRerankResponse
        {
            [JsonPropertyName("data")]
            public required List<VoyageRerankData> Data { get; set; }
        }

        public class VoyageRerankData
        {
            [JsonPropertyName("index")]
            public int Index { get; set; }

            [JsonPropertyName("relevance_score")]
            public double RelevanceScore { get; set; }
        }
    }
}
