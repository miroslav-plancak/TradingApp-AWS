using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Infrastructure;
using TradingApp.Infrastructure.Helpers;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Models;

namespace TradingApp.API.Hubs
{
    public class AiChatHub : Hub
    {
        private readonly ILogger<AiChatHub> _logger;
        private readonly AnthropicClient _anthropicClient;
        private readonly IChunkRetrievalService _chunkRetrievalService;
        private readonly IAsyncPolicy _resiliencePolicy;

        public AiChatHub
        (
            ILogger<AiChatHub> logger,
            AnthropicClient anthropicClient,
            IChunkRetrievalService chunkRetrievalService,
            [FromKeyedServices(ResiliencePolicyKey.AnthropicAPI)] IAsyncPolicy resiliencePolicy
        )
        {
            _logger = logger;
            _anthropicClient = anthropicClient;
            _chunkRetrievalService = chunkRetrievalService;
            _resiliencePolicy = resiliencePolicy;
        }

        public override Task OnConnectedAsync()
        {
            _logger.LogInformation("AiChatHub client connected | ConnectionId: {ConnectionId}", Context.ConnectionId);

            return base.OnConnectedAsync();
        }

        public async IAsyncEnumerable<string> Ask(string userQuestion)
        {
            var retrievalResult = new RetrievalResult { ChunkFallbacks = [], FullFileContents = [] };

            try
            {
                retrievalResult = await _chunkRetrievalService.RetrieveRelevantContextAsync(userQuestion);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure retrieving context for question: {UserQuestion}", userQuestion);
            }

            var parameters = new MessageCreateParams
            {
                Model = "claude-sonnet-5",
                MaxTokens = 4096,
                System = SystemPromptBuilder.BuildSystemPrompt(retrievalResult),
                Messages = [new() { Role = Role.User, Content = userQuestion }]
            };
            //TODO: need to find a way to wrap this in a _resiliencePolicy
            await foreach (var streamEvent in _anthropicClient.Messages.CreateStreaming(parameters))
            {
                if (streamEvent.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                {
                    yield return text.Text;
                }
            }
        }
    }
}
