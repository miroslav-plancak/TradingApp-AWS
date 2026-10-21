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

        public async Task LogSectionAsync<T>
        (
            string logName,
            string title,
            T content
        )
        {
            var filePath = Path.Combine(_logDirectory, $"{logName}.txt");

            try
            {
                var body = content is string s ? s : JsonSerializer.Serialize(content, new JsonSerializerOptions { WriteIndented = true });
                var block = BuildBlock(title, body);

                await _writeLogLock.WaitAsync();

                try
                {
                    await File.AppendAllTextAsync(filePath, block);
                }
                finally
                {
                    _writeLogLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write debug log section | LogName: {LogName} | Title: {Title} | FilePath: {FilePath}",
                    logName, title, filePath);
            }
        }

        private const int DividerWidth = 100;

        private static string BuildBlock
        (
            string title,
            string content
        )
        {
            var divider = new string('=', DividerWidth);
            var stringBuilder = new StringBuilder();

            stringBuilder.AppendLine(divider);
            stringBuilder.AppendLine(WrapText($"[{DateTime.Now:dd-MM-yyyy HH:mm:ss}] {title}", DividerWidth));
            stringBuilder.AppendLine(divider);
            stringBuilder.AppendLine(WrapText(content, DividerWidth));
            stringBuilder.AppendLine();

            return stringBuilder.ToString();
        }

        private static string WrapText(string text, int width)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var wrapped = new StringBuilder();

            foreach (var line in text.Split('\n'))
            {
                if (line.Length <= width)
                {
                    wrapped.AppendLine(line.TrimEnd('\r'));
                    continue;
                }

                var currentLine = new StringBuilder();

                foreach (var word in line.Split(' '))
                {
                    if (currentLine.Length > 0 && currentLine.Length + 1 + word.Length > width)
                    {
                        wrapped.AppendLine(currentLine.ToString());
                        currentLine.Clear();
                    }

                    if (currentLine.Length > 0)
                    {
                        currentLine.Append(' ');
                    }

                    currentLine.Append(word);
                }

                if (currentLine.Length > 0)
                {
                    wrapped.AppendLine(currentLine.ToString());
                }
            }

            return wrapped.ToString().TrimEnd('\r', '\n');
        }
    }
}