using StackExchange.Redis;
using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Interfaces
{
    public interface IChunkRetrievalService
    {
        Task<List<RetrievedChunk>> RetrieveRelevantChunksAsync(string userQuestion);
    }
}
