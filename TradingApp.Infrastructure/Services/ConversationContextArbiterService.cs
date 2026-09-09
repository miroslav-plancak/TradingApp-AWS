using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingApp.Business.DTOs.ConversationChunk;
using TradingApp.Infrastructure.Helpers;
using TradingApp.Infrastructure.Interfaces;

namespace TradingApp.Infrastructure.Services
{
    public class ConversationContextArbiterService : IConversationContextArbiterService
    {
        private readonly ILogger<ConversationContextArbiterService> _logger;
        private readonly AnthropicClient _anthropicClient;
        private readonly IAsyncPolicy _resiliencePolicy;
        private readonly IFileDebugLogger _fileDebugLogger;
        //TODO: move this and the other LLM query to some enviornment variables so that they can be changed without re-deployment.
        private const string _arbiterSystemInstruction =
          "Decide whether the user's question can be FULLY and accurately answered using only the chunks provided below — " +
          "not merely related to them, but sufficient to answer completely. Each chunk is a JSON object with a \"Key\" field.\r\n" +
          "Respond with exactly this JSON shape: {\"chunkKeys\": [...]}. Provide no explanation for your choice — pure JSON only.\r\n" +
          "- If one or more chunks together are sufficient to fully answer the question, list the \"Key\" value of each chunk you used.\r\n" +
          "- If the chunks are only partially relevant, or you are not confident they fully cover the question, return {\"chunkKeys\": []}.\r\n" +
          "- Only cite \"Key\" values that literally appear in the chunks provided — never invent one.";

        private static string SerializeExistingConversationChunks(List<CreatedConversationChunkResponseDTO> createdConversationChunks)
        {
            return JsonSerializer.Serialize(createdConversationChunks);
        }

        public ConversationContextArbiterService
        (
            ILogger<ConversationContextArbiterService> logger,
            AnthropicClient anthropicClient,
            [FromKeyedServices(ResiliencePolicyKey.AnthropicAPI)] IAsyncPolicy resiliencePolicy,
            IFileDebugLogger fileDebugLogger)
        {
            _logger = logger;
            _anthropicClient = anthropicClient;
            _resiliencePolicy = resiliencePolicy;
            _fileDebugLogger = fileDebugLogger;
        }

        public async Task<List<CreatedConversationChunkResponseDTO>> DetermineSufficientChunksAsync
        (
            string userQuestion,
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
                Messages = [new() { Role = Role.User, Content = $"{userQuestion} \n\n Existing Chunks(JSON array): \n {conversationChunksContext}" }]
            };

            try
            {
                var messageResponse = await _resiliencePolicy.ExecuteAsync(async () =>
                {
                    return await _anthropicClient.Messages.Create(parameters);
                });

                var firstBlock = messageResponse.Content.Count > 0 ? messageResponse.Content[0] : null;

                if (firstBlock is not null && firstBlock.TryPickText(out var textblock))
                {
                    var responseText = ExtractJsonObjectFromArbiterResponse(textblock.Text.Trim());

                    try
                    {
                        var arbiterResponse = JsonSerializer.Deserialize<ArbiterResponse>(responseText);


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
                        _logger.LogWarning(ex, "Failed to parse arbiter response as JSON | Response: {ResponseText}", responseText);
                    }

                    return [];
                }

                return [];
            }
            catch (Exception ex) when (ResiliencePolicyBuilder.IsTransientAnthropicApiException(ex))
            {
                _logger.LogWarning(ex, "Known transient Anthropic failure while dispatching query classification | Question: {UserQuestion}", userQuestion);
                return [];
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure while dispatching query classification | Question: {UserQuestion}", userQuestion);
                return [];
            }
        }

        private static string ExtractJsonObjectFromArbiterResponse(string text)
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');

            if (start == -1 || end == -1 || end < start)
            {
                return text;
            }

            return text.Substring(start, end - start + 1);
        }

        public class ArbiterResponse
        {
            [JsonPropertyName("chunkKeys")]
            public List<string> ChunkKeys { get; set; } = [];
        }
    }
}
