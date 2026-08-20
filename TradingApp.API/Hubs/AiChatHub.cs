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
            var chunks = await _chunkRetrievalService.RetrieveRelevantChunksAsync(userQuestion);
            LogRetrievedChunks(chunks);
    
            var parameters = new MessageCreateParams
            {
                Model = "claude-sonnet-5",
                MaxTokens = 4096,
                System = BuildSystemPrompt(chunks),
                Messages = [new() { Role = Role.User, Content = userQuestion }]
            };

            await foreach(var streamEvent in _anthropicClient.Messages.CreateStreaming(parameters))
            {
                if(streamEvent.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                {
                    yield return text.Text;
                }
            }
        }

        private static string BuildSystemPrompt(List<RetrievedChunk> chunks)
        {
            var context = string.Join("\n\n", chunks.Select(c => $"Source: {c.SourceFile}\n{c.Content}"));
            return $"Answer the user's question using the following code context if it's relevant." +
                $" If the context doesn't contain the answer, say so instead of guessing.\n\n{context}";
        }

        private void LogRetrievedChunks(List<RetrievedChunk> chunks)
        {
            int i = 1;

            foreach(var chunk in chunks)
            {
                _logger.LogInformation("AiChatHub | RetrievedRelevantChunk | SourceFile [{i}]: {ChunkProperty}", i,chunk.SourceFile);
                _logger.LogInformation("AiChatHub | RetrievedRelevantChunk | Content [{i}]: {ChunkProperty}", i, chunk.Content);
                _logger.LogInformation("AiChatHub | RetrievedRelevantChunk | Score [{i}]: {ChunkProperty}", i, chunk.Score);
                i++;
            }
        }
    }
}
