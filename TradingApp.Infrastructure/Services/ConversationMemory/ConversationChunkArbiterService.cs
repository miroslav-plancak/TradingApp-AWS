using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TradingApp.Business.DTOs.ConversationChunk;
using TradingApp.Infrastructure.Helpers.ConversationMemory;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.ConversationMemory;
using TradingApp.Infrastructure.Models.ConversationMemory;

namespace TradingApp.Infrastructure.Services.ConversationMemory
{
    public class ConversationChunkArbiterService : IConversationChunkArbiterService
    {
        private readonly ILogger<ConversationChunkArbiterService> _logger;
        private readonly IAnthropicApiService _anthropicApiService;
        private readonly IFileDebugLogger _fileDebugLogger;
        //TODO: move this and the other LLM query to some enviornment variables so that they can be changed without re-deployment.
        private const string _arbiterSystemInstruction =
          "Decide whether the user's question can be FULLY and accurately answered using only the chunks provided below — " +
          "not merely related to them, but sufficient to answer completely. Each chunk is a JSON object with a \"Key\" field.\r\n" +
          "Respond with exactly this JSON shape: {\"chunkKeys\": [...]}. Provide no explanation for your choice — pure JSON only.\r\n" +
          "- If one or more chunks together are sufficient to fully answer the question, list the \"Key\" value of each chunk you used.\r\n" +
          "- If the chunks are only partially relevant, or you are not confident they fully cover the question, return {\"chunkKeys\": []}.\r\n" +
          "- Only cite \"Key\" values that literally appear in the chunks provided — never invent one.";

        public ConversationChunkArbiterService
        (
            ILogger<ConversationChunkArbiterService> logger,
            IFileDebugLogger fileDebugLogger,
            IAnthropicApiService anthropicApiService
        )
        {
            _logger = logger;
            _fileDebugLogger = fileDebugLogger;
            _anthropicApiService = anthropicApiService;
        }

        public async Task<List<CreatedConversationChunkResponseDTO>> DetermineSufficientChunksAsync
        (
            string userMessage,
            List<CreatedConversationChunkResponseDTO> existingChunks
        )
        {
            if (existingChunks.Count == 0) return [];

            var conversationChunksContext = SerializeExistingConversationChunks(existingChunks);

            var parameters = new MessageCreateParams
            {
                Model = "claude-haiku-4-5",
                MaxTokens = 512,
                System = _arbiterSystemInstruction,
                Messages = [new() { Role = Role.User, Content = $"{userMessage} \n\n Existing Chunks(JSON array): \n {conversationChunksContext}" }]
            };

            try
            {
                var result = await _anthropicApiService.DispatchPromptAsync(parameters, userMessage);
                var extractedJson = LlmJsonExtractor.ExtractJsonObject(result);

                try
                {
                    var arbiterResponse = JsonSerializer.Deserialize<ArbiterResponse>(extractedJson);

                    await _fileDebugLogger.LogSectionAsync("2b-arbiter-picked-keys", "ConversationChunk keys picked by the LLM",
                        RetrievalResultLogFormatter.FormatArbiterResponseIntoFileLog(arbiterResponse));

                    if (arbiterResponse?.ChunkKeys?.Count > 0)
                    {
                        var citedChunkConversationKeys = arbiterResponse.ChunkKeys.ToHashSet();
                        return existingChunks.Where(x => citedChunkConversationKeys.Contains(x.Key)).ToList();
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "ArbiterResponseJsonParseFailure | Response: {ResponseText}", extractedJson);
                }

                return [];
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ChunkArbitrationUnexpectedFailure | Question: {UserMessage}", userMessage);
                return [];
            }
        }

        private static string SerializeExistingConversationChunks(List<CreatedConversationChunkResponseDTO> createdConversationChunks)
        {
            return JsonSerializer.Serialize(createdConversationChunks);
        }
    }
}
