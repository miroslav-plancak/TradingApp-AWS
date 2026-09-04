using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Conversation;
using TradingApp.Business.Interfaces.Services;
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
        private readonly IConversationService _conversationService;
        public AiChatHub
        (
            ILogger<AiChatHub> logger,
            AnthropicClient anthropicClient,
            IChunkRetrievalService chunkRetrievalService,
            [FromKeyedServices(ResiliencePolicyKey.AnthropicAPI)] IAsyncPolicy resiliencePolicy,
            IConversationService conversationService
        )
        {
            _logger = logger;
            _anthropicClient = anthropicClient;
            _chunkRetrievalService = chunkRetrievalService;
            _resiliencePolicy = resiliencePolicy;
            _conversationService = conversationService;
        }

        public override Task OnConnectedAsync()
        {
            _logger.LogInformation("AiChatHub client connected | ConnectionId: {ConnectionId}", Context.ConnectionId);

            return base.OnConnectedAsync();
        }
        private async Task NotifyConversationStartedAsync(Guid newConversationId)
        {
            try
            {
                await Clients.Caller.SendAsync("ConversationStarted", newConversationId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify client of new conversation {ConversationId}", newConversationId);
                //await _conversationService.DeleteConversationAsync(newConversationId); //TODO: implement this delete
                throw;
            }
        }

        public async IAsyncEnumerable<string> Ask(string userQuestion, Guid? conversationId, Guid? clientRequestId)
        {
            var retrievalResult = new RetrievalResult { ChunkFallbacks = [], FullFileContents = [] };
            CreatedConversationResponseDTO existingConversation;

            try
            {
                if(conversationId is null)
                {
                    existingConversation = await _conversationService.CreateConversationAsync(userQuestion, clientRequestId);
                    await NotifyConversationStartedAsync(existingConversation.ConversationId);
                }
                else
                {
                    try 
                    {
                        existingConversation = await _conversationService.GetConversationByIdAsync(conversationId.Value);
                    }
                    catch (KeyNotFoundException)
                    {
                        existingConversation = await _conversationService.CreateConversationAsync(userQuestion, clientRequestId);
                        await NotifyConversationStartedAsync(existingConversation.ConversationId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to establish conversation context for question: {UserQuestion}", userQuestion);
                throw new HubException("There was an error processing your request. Please try again.");
            }

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
                throw new HubException("There was an error processing your request. Please try again.");

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

                    if (streamFailed)
                        throw new HubException("The response was interrupted partway through. Please try again.");

                    if (!hasNext)
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
