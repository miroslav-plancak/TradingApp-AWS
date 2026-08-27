using TradingApp.Infrastructure.Enums;

namespace TradingApp.Infrastructure.Interfaces
{
    public interface IQueryRoutingService
    {
        Task<LlmQueryClassification> LlmQueryRouteAsync(string userQuestion);
    }
}
