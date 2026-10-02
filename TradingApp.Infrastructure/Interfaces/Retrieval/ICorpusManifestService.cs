using TradingApp.Infrastructure.Models.Ingestion;

namespace TradingApp.Infrastructure.Interfaces.Retrieval
{
    public interface ICorpusManifestService
    {
        Task<List<ProcessedCorpusSourceFile>> GetEntireCorpusManifestAsync();
    }
}
