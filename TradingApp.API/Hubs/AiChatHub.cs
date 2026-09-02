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

            IAsyncEnumerator<RawMessageStreamEvent> enumerator = null;
            string firstText = null;
            var bootstrapFailed = false;

            try
            {
                (enumerator, firstText) = await _resiliencePolicy.ExecuteAsync(async () =>
                {
                    var e = _anthropicClient.Messages.CreateStreaming(parameters).GetAsyncEnumerator();

                    while (await e.MoveNextAsync())
                    {
                        if (e.Current.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                        {
                            return (e, text.Text); 
                        }
                    }

                    return (e, (string)null);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Streaming failure before any content was produced for question: {UserQuestion}", userQuestion);
                bootstrapFailed = true;
            }

            if (bootstrapFailed || enumerator is null)
                yield break;  

            await using (enumerator)
            {
                var hasYieldedAnyContent = false;

                if (firstText is not null)
                {
                    hasYieldedAnyContent = true;
                    yield return firstText;
                }

                while (true)
                {
                    bool hasNext;
                    var streamFailed = false;

                    try
                    {
                        hasNext = await enumerator.MoveNextAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Streaming failure while answering question: {UserQuestion} | AnyContentYielded: {HasYieldedAnyContent}",
                            userQuestion, hasYieldedAnyContent);
                        streamFailed = true;
                        hasNext = false;
                    }

                    if (streamFailed || !hasNext)
                        yield break;

                    if (enumerator.Current.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                    {
                        hasYieldedAnyContent = true;
                        yield return text.Text;
                    }
                }
            }
        }
    }
}
