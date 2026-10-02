using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TradingApp.Infrastructure.Helpers.ConversationMemory;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.Ingestion;
using TradingApp.Infrastructure.Models.Ingestion;

namespace TradingApp.Infrastructure.Services.Ingestion
{
    public class CorpusFileDescriptionService : ICorpusFileDescriptionService
    {
        private readonly ILogger<CorpusFileDescriptionService> _logger;
        private readonly IAnthropicApiService _anthropicApiService;
        private readonly IFileDebugLogger _fileDebugLogger;

        public CorpusFileDescriptionService
        (
            ILogger<CorpusFileDescriptionService> logger,
            IAnthropicApiService anthropicApiService,
            IFileDebugLogger fileDebugLogger
        )
        {
            _logger = logger;
            _anthropicApiService = anthropicApiService;
            _fileDebugLogger = fileDebugLogger;
        }

        public async Task<List<CorpusFileDescription>> ProcessRawCorpusSourceFilesAsync(RawCorpusSourceFile[] rawSourceFiles)
        {

            var parameters = new MessageCreateParams
            {
                Model = "claude-haiku-4-5",
                MaxTokens = 4096,
                System = SystemPromptBuilder.CorpusFileDescriptionSystemInstruction,
                Messages = [new() { Role = Role.User, Content = BuildSourceFilesPayload(rawSourceFiles)}]
            };

            try
            {
                var result = await _anthropicApiService.DispatchPromptAsync(parameters);
                var extractedJson = LlmJsonExtractor.ExtractJsonArray(result);

                try
                {
                    var processedCorpusFiles = JsonSerializer.Deserialize<List<CorpusFileDescription>>(extractedJson);

                    await _fileDebugLogger.LogSectionAsync("corpus-file-descriptor", "Raw source files were distiled into the following format:",
                        string.Join("\n", (processedCorpusFiles ?? []).Select(x => $"{x.Name}: {x.Description}")));

                    if (processedCorpusFiles == null || processedCorpusFiles.Count == 0)
                    {
                        _logger.LogWarning("ProcessingOfRawCorpusSourceFilesReturnedEmptyResult | DescriptionsCount: {DescriptionsCount}", processedCorpusFiles?.Count);
                        return [];
                    }

                    return processedCorpusFiles;
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "ProcessingOfRawCorpusSourceFilesJsonParseFailure | Response: {ResponseText}", extractedJson);
                }

                return [];
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProcessingOfRawCorpusSourceFilesUnexpectedFailure | Question: {UserMessage}", rawSourceFiles);
                return [];
            }
        }

        private string BuildSourceFilesPayload(RawCorpusSourceFile[] rawSourceFiles)
        {
            if (rawSourceFiles.Count() == 0) return "No source files provided.";

            return string.Join("\n", rawSourceFiles.Select((sourceFile, i) =>
              $"#{i + 1}\n: " +
              $"\n FileName:  {sourceFile.Name}" +
              $"\n\n Content: {sourceFile.Content} "
          ));
        }
    }
}
