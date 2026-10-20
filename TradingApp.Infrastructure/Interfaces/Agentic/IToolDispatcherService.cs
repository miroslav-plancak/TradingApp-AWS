using TradingApp.Infrastructure.Models.Agentic;

namespace TradingApp.Infrastructure.Interfaces.Agentic
{
    public interface IToolDispatcherService
    {
        Task<ToolDispatchResponse> DispatchToolCallAsync(ToolDispatchRequest request);
    }
}
