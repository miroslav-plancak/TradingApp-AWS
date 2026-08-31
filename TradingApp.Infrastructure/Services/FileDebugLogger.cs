using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using TradingApp.Infrastructure.Interfaces;

namespace TradingApp.Infrastructure.Services
{
    public class FileDebugLogger : IFileDebugLogger
    {
        private readonly string _logDirectory;
        private readonly SemaphoreSlim _writeLogLock = new SemaphoreSlim(1, 1);
        private readonly ILogger<FileDebugLogger> _logger;

        public FileDebugLogger(ILogger<FileDebugLogger> logger)
        {
            _logger = logger;
            _logDirectory = Path.Combine(AppContext.BaseDirectory, "debug-logs");
            Directory.CreateDirectory(_logDirectory);
        }

        public async Task LogSectionAsync<T>(string logName, string title, T content)
        {
            var filePath = Path.Combine(_logDirectory, $"{logName}.txt");
            var body = content is string s ? s : JsonSerializer.Serialize(content, new JsonSerializerOptions { WriteIndented = true });
            var block = BuildBlock(title, body);

            await _writeLogLock.WaitAsync();

            try
            {
                await File.AppendAllTextAsync(filePath, block);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write debug log section | LogName: {LogName} | Title: {Title} | FilePath: {FilePath}",
                    logName, title, filePath);
            }
            finally
            {
                _writeLogLock.Release();
            }
        }

        private static string BuildBlock(string title, string content)
        {
            var divider = new string('=', 80);
            var stringBuilder = new StringBuilder();

            stringBuilder.AppendLine(divider);
            stringBuilder.AppendLine($"[{DateTime.Now:dd-MM-yyyy HH:mm:ss}] {title}");
            stringBuilder.AppendLine(divider);
            stringBuilder.AppendLine(content);
            stringBuilder.AppendLine();

            return stringBuilder.ToString();
        }
    }
}