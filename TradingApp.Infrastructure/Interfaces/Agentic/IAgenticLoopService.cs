namespace TradingApp.Infrastructure.Interfaces.Agentic
{
    public interface IAgenticLoopService
    {
        Task<string?> RunAgenticLoopAsync(string userMessage);
    }
}
