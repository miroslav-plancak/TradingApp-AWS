namespace TradingApp.Infrastructure.Interfaces
{
    public interface IVoyageApiService
    {
        Task<TResponse> DispatchRequestAsync<TRequest, TResponse>(string endPoint, TRequest request, CancellationToken cancellationToken = default);
    }
}
