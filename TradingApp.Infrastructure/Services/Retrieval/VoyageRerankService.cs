using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.Retrieval;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Services.Retrieval
{
    public class VoyageRerankService : IVoyageRerankService
    {
        private readonly ILogger<VoyageRerankService> _logger;
        private readonly IVoyageApiService _voyageApiService;

        public VoyageRerankService
        (
            ILogger<VoyageRerankService> logger,
            IVoyageApiService voyageApiService)
        {
            _logger = logger;
            _voyageApiService = voyageApiService;
        }

        public async Task<IReadOnlyList<RerankResult>> RerankAsync
        (
            string query,
            IReadOnlyList<string> documents,
            CancellationToken ct = default
        )
        {
            var request = new VoyageRerankRequest
            {
                Query = query,
                Documents = documents.ToList(),
                Model = "rerank-2.5"

            };

            try
            {
                var response = await _voyageApiService.DispatchRequestAsync<VoyageRerankRequest,VoyageRerankResponse>("rerank", request, ct);

                return response.Data
                    .Select(x => new RerankResult { Index = x.Index, RelevanceScore = x.RelevanceScore })
                    .ToList();
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, "Voyage rerank request failed | DocumentCount: {DocumentCount}", documents.Count);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failture occurred while dispatching Voyage rerank request | DocumentCount: {DocumentCount}",
                    documents.Count);
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

        private class VoyageRerankData
        {
            [JsonPropertyName("index")]
            public int Index { get; set; }

            [JsonPropertyName("relevance_score")]
            public double RelevanceScore { get; set; }
        }
    }
}
