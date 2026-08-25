using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Interfaces
{
    public interface IVoyageRerankService
    {
        Task<IReadOnlyList<RerankResult>> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken ct = default);
    }
}
