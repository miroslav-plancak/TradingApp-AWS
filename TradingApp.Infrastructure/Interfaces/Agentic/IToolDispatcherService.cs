using TradingApp.Infrastructure.Models.Agentic;

namespace TradingApp.Infrastructure.Interfaces.Agentic
{
    public interface IToolDispatcherService
    {
        Task<(string Text, bool IsError)> DispatchToolCallAsync(ToolDispatchRequest request);
    }
}
