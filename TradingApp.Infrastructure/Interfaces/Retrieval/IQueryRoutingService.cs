using TradingApp.Infrastructure.Enums;

namespace TradingApp.Infrastructure.Interfaces.Retrieval
{
    public interface IQueryRoutingService
    {
        Task<LlmQueryClassification> LlmQueryRouteAsync(string userMessage);
    }
}
