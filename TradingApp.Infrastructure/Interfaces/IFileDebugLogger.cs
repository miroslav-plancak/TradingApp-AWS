namespace TradingApp.Infrastructure.Interfaces
{
    public interface IFileDebugLogger
    {
        Task LogSectionAsync<T>(string logName, string title, T content);
    }
}
