using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Interfaces
{
    public interface IChunkRetrievalService
    {
        Task<RetrievalResult> RetrieveRelevantChunksAsync(string userQuestion);
    }
}
