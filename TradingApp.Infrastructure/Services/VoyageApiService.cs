using Microsoft.Extensions.DependencyInjection;
using Polly;
using System.Net.Http.Json;
using TradingApp.Infrastructure.Interfaces;

namespace TradingApp.Infrastructure.Services
{
    public class VoyageApiService : IVoyageApiService
    {
        private readonly HttpClient _httpClient;
        private readonly IAsyncPolicy _resiliencePolicy;

        public VoyageApiService
        (
            HttpClient httpClient,
            [FromKeyedServices(ResiliencePolicyKey.VoyageAPI)] IAsyncPolicy resiliencePolicy
        )
        {
            _httpClient = httpClient;
            _resiliencePolicy = resiliencePolicy;
        }

        public async Task<TResponse> DispatchRequestAsync<TRequest, TResponse>
        (
           string endPoint,
           TRequest request,
           CancellationToken ct = default
        )
        {
            var response = await _resiliencePolicy.ExecuteAsync(async (policyCancellationToken) =>
            {
                var httpResponse = await _httpClient.PostAsJsonAsync(endPoint, request, policyCancellationToken);
                httpResponse.EnsureSuccessStatusCode();
                return httpResponse;
            }, ct);

            var payload = await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: ct)
                ?? throw new InvalidOperationException("Voyage API returned an empty response.");

            return payload;
        }
    }
}
