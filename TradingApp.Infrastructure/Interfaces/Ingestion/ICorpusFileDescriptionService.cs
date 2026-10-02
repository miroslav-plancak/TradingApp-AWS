using TradingApp.Infrastructure.Models.Ingestion;

namespace TradingApp.Infrastructure.Interfaces.Ingestion
{
    public interface ICorpusFileDescriptionService
    {
        Task<List<CorpusFileDescription>> ProcessRawCorpusSourceFilesAsync(RawCorpusSourceFile[] rawSourceFiles);
    }
}
