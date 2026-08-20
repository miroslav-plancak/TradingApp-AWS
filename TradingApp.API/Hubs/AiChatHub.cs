using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Models;

namespace TradingApp.API.Hubs
{
    public class AiChatHub : Hub
    {
        private readonly ILogger<AiChatHub> _logger;
        private readonly AnthropicClient _anthropicClient;
        private readonly IChunkRetrievalService _chunkRetrievalService;
        public AiChatHub
        (
            ILogger<AiChatHub> logger,
            AnthropicClient anthropicClient,
            IChunkRetrievalService chunkRetrievalService
        )
        {
            _logger = logger;
            _anthropicClient = anthropicClient;
            _chunkRetrievalService = chunkRetrievalService;
        }

        public override Task OnConnectedAsync()
        {
            _logger.LogInformation("AiChatHub client connected | ConnectionId: {ConnectionId}", Context.ConnectionId);

            return base.OnConnectedAsync();
        }

        public async IAsyncEnumerable<string> Ask(string userQuestion)
        {
            var retrievalResult = await _chunkRetrievalService.RetrieveRelevantChunksAsync(userQuestion);
            LogRetrievalResult(retrievalResult);

            var parameters = new MessageCreateParams
            {
                Model = "claude-sonnet-5",
                MaxTokens = 4096,
                System = BuildSystemPrompt(retrievalResult),
                Messages = [new() { Role = Role.User, Content = userQuestion }]
            };

            await foreach (var streamEvent in _anthropicClient.Messages.CreateStreaming(parameters))
            {
                if (streamEvent.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                {
                    yield return text.Text;
                }
            }
        }

        private static string BuildSystemPrompt(RetrievalResult retrievalResult)
        {
            var chunks = string.Join("\n\n", retrievalResult.ChunkFallbacks.Select(c => $"Source: {c.SourceFile}\n{c.Content}"));
            var fullFiles = string.Join("\n\n", retrievalResult.FullFileContents.Select(c => $"FullFiles - FileName: {c.Key}\n{c.Value}"));
            var fullContext = string.Join("\n\n", new[] { chunks, fullFiles }.Where(section => !string.IsNullOrWhiteSpace(section)));

            return $"Answer the user's question using the following code context if it's relevant." +
                $" If the context doesn't contain the answer, say so instead of guessing.\n\n{fullContext}";
        }

        private void LogRetrievalResult(RetrievalResult retrievalResult)
        {
            var i = 1;

            foreach (var chunk in retrievalResult.ChunkFallbacks)
            {
                _logger.LogInformation("AiChatHub | RetrievedRelevantChunk | SourceFile [{i}]: {ChunkProperty}", i, chunk.SourceFile);
                _logger.LogInformation("AiChatHub | RetrievedRelevantChunk | Content [{i}]: {ChunkProperty}", i, chunk.Content);
                _logger.LogInformation("AiChatHub | RetrievedRelevantChunk | Score [{i}]: {ChunkProperty}", i, chunk.Score);
                i++;
            }

            foreach (var fileName in retrievalResult.FullFileContents.Keys)
            {
                _logger.LogInformation("AiChatHub | ExpandedToFullFile | SourceFile: {SourceFile}", fileName);
            }
        }
    }
}
