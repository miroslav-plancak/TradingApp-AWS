using Anthropic.Models.Messages;
using TradingApp.Infrastructure.Models.Agentic;

namespace TradingApp.Infrastructure.Interfaces.Agentic
{
    public interface IAgenticLoopService
    {
        IAsyncEnumerable<string> RunAgenticLoopAsync(AgenticLoopRequest request);
        Task<string> HandleGetAllIndexedFileNames();
    }
}
