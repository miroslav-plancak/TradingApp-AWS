namespace TradingApp.Infrastructure.Interfaces.Retrieval
{
    public interface IQueryDecompositionService
    {
        Task<List<string>> DecomposeQueryAsync(string userMessage);
    }
}
