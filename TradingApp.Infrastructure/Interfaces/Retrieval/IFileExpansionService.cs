namespace TradingApp.Infrastructure.Interfaces.Retrieval
{
    public interface IFileExpansionService
    {
        Task<Dictionary<string, string>> GetExistingFullFileContentsMapAsync(IEnumerable<string?> distinctFileNames, CancellationToken ct = default);
    }
}
