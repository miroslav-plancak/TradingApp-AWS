using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Interfaces
{
    public interface IChunkRetrievalService
    {
        Task<RetrievalResult> RetrieveRelevantContextAsync(string userQuestion);
    }
}
