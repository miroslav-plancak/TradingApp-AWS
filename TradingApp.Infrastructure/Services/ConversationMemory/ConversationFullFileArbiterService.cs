using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TradingApp.Business.DTOs.ConversationFullFile;
using TradingApp.Infrastructure.Helpers.ConversationMemory;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.ConversationMemory;
using TradingApp.Infrastructure.Models.ConversationMemory;

namespace TradingApp.Infrastructure.Services.ConversationMemory
{
    public class ConversationFullFileArbiterService : IConversationFullFileArbiterService
    {
        private readonly ILogger<ConversationFullFileArbiterService> _logger;
        private readonly IAnthropicApiService _anthropicApiService;
        private readonly IFileDebugLogger _fileDebugLogger;

        public ConversationFullFileArbiterService
        (
            ILogger<ConversationFullFileArbiterService> logger,
            IFileDebugLogger fileDebugLogger,
            IAnthropicApiService anthropicApiService
        )
        {
            _logger = logger;
            _fileDebugLogger = fileDebugLogger;
            _anthropicApiService = anthropicApiService;
        }


        public async Task<List<CreatedConversationFullFileResponseDTO>> DetermineSufficientFullFilesAsync
        (
            string userMessage,
            List<CreatedConversationFullFileResponseDTO> existingFullFiles
        )
        {
            if (existingFullFiles.Count == 0) return [];

            var fullFilesContext = SerializeExistingConversationChunks(existingFullFiles);

            var parameters = new MessageCreateParams
            {
                Model = "claude-haiku-4-5",
                MaxTokens = 512,
                System = SystemPromptBuilder.FullFileArbiterSystemInstruction,
                Messages = [new() { Role = Role.User, Content = BuildFullFileArbiterUserMessage(userMessage, fullFilesContext) }]
            };

            try
            {
                var result = await _anthropicApiService.DispatchPromptAsync(parameters, userMessage);
                var extractedJson = LlmJsonExtractor.ExtractJsonObject(result);

                try
                {
                    var arbiterResponse = JsonSerializer.Deserialize<FullFileArbiterResponse>(extractedJson);

                    await _fileDebugLogger.LogSectionAsync("3b-full-file-arbiter-picked-keys", "FullFile keys picked by the LLM",
                        RetrievalResultLogFormatter.FormatFullFileArbiterResponseIntoFileLog(arbiterResponse));

                    if (arbiterResponse?.SourceFiles?.Count > 0)
                    {
                        var citedFullFileKeys = arbiterResponse.SourceFiles.ToHashSet();
                        return existingFullFiles.Where(x => citedFullFileKeys.Contains(x.SourceFile)).ToList();
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "FullFileArbiterResponseJsonParseFailure | Response: {ResponseText}", extractedJson);
                }

                return [];
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FullFileArbitrationUnexpectedFailure | Question: {UserMessage}", userMessage);
                return [];
            }
        }

        private static string SerializeExistingConversationChunks(List<CreatedConversationFullFileResponseDTO> createdFullFiles)
        {
            return JsonSerializer.Serialize(createdFullFiles);
        }

        private static string BuildFullFileArbiterUserMessage(string userMessage, string fullFilesContext)
        {
            return $"{userMessage} \n\n Existing Source Files(JSON array): \n {fullFilesContext}";
        }
    }
}
