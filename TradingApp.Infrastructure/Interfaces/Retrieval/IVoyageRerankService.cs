using TradingApp.Infrastructure.Models.Retrieval;
using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Interfaces.Retrieval
{
    public interface IVoyageRerankService
    {
        Task<IReadOnlyList<RerankResult>> RerankAsync
        (
            string query,
            IReadOnlyList<string> documents,
            CancellationToken ct = default
        );
    }
}
