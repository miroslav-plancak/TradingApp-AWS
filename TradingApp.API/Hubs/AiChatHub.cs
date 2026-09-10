using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Conversation;
using TradingApp.Business.DTOs.ConversationMessage;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Domain.Models.Enums;
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
        private readonly IFileDebugLogger _fileDebugLogger;

        public AiChatHub
        (
            ILogger<AiChatHub> logger,
            IFileDebugLogger fileDebugLogger,
            AnthropicClient anthropicClient,
            IChunkRetrievalService chunkRetrievalService,
            [FromKeyedServices(ResiliencePolicyKey.AnthropicAPI)] IAsyncPolicy resiliencePolicy,
            IConversationService conversationService
        )
        {
            _logger = logger;
            _fileDebugLogger = fileDebugLogger;
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

                try
                {
                    await _conversationService.DeleteConversationByIdAsync(newConversationId);
                }
                catch (Exception deleteEx)
                {
                    _logger.LogError(deleteEx, "Failed to clean up orphaned conversation {ConversationId} after notify failure", newConversationId);
                }

                throw;
            }
        }

        public async IAsyncEnumerable<string> Ask(string userQuestion, Guid? conversationId, Guid? clientRequestId)
        {
            if (string.IsNullOrWhiteSpace(userQuestion))
                throw new HubException("Question cannot be empty.");

            var retrievalResult = new RetrievalResult { ChunkFallbacks = [], FullFileContents = [] };
            CreatedConversationResponseDTO existingConversation;
            CreatedConversationMessageResponseDTO userCreatedConversationMessage;
            CreatedConversationMessageResponseDTO assistantCreatedConversationMessage;
            List<ConversationMessageDTO> conversationMessagesHistory = [];
            var isNewConversation = false;

            //1. we create a conversation or load existing
            try
            {
                if (conversationId == null)
                {
                    existingConversation = await _conversationService.CreateConversationAsync(userQuestion, clientRequestId);
                    await NotifyConversationStartedAsync(existingConversation.ConversationId);
                    isNewConversation = true;
                }
                else
                {
                    try
                    {
                        existingConversation = await _conversationService.GetConversationByIdAsync(conversationId.Value);
                        isNewConversation = false;
                    }
                    catch (KeyNotFoundException)
                    {
                        existingConversation = await _conversationService.CreateConversationAsync(userQuestion, clientRequestId);
                        await NotifyConversationStartedAsync(existingConversation.ConversationId);
                        isNewConversation = true;
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
                retrievalResult = await _chunkRetrievalService.RetrieveRelevantContextAsync(userQuestion, existingConversation.ConversationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure retrieving context for question: {UserQuestion}", userQuestion);
            }

            //4. retrieve from the permanence source rows of role/content (role/body in db) for both user/assistant, sorted by createdAt ascending + append to the end current input question from the Ask
            try
            {
                conversationMessagesHistory = await _conversationService.GetConversationMessagesAsync(existingConversation.ConversationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure retrieving conversation messages for question: {UserQuestion}", userQuestion);
            }

            conversationMessagesHistory.Add(
              new ConversationMessageDTO
              {
                  Role = ConversationMessageRole.User.ToString().ToLower(),
                  Content = userQuestion
              }
            );

            await _fileDebugLogger.LogSectionAsync("0-conversation-history", $"Current user/assistant correspodence:",
                            RetrievalResultLogFormatter.FormatCurrentConversationMessagesIntoFileLog(conversationMessagesHistory));

            var parameters = new MessageCreateParams
            {
                Model = "claude-sonnet-5",
                MaxTokens = 4096,
                System = SystemPromptBuilder.BuildSystemPrompt(retrievalResult),
                Messages = ToAnthropicMessageParams(conversationMessagesHistory)
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
            {
                if (isNewConversation)
                {
                    try
                    {
                        await _conversationService.DeleteConversationByIdAsync(existingConversation.ConversationId);
                    }
                    catch (Exception deleteEx)
                    {
                        _logger.LogError(deleteEx, "Failed to clean up orphaned conversation {ConversationId} after bootstrap failure", existingConversation.ConversationId);
                    }
                }

                throw new HubException("There was an error processing your request. Please try again.");
            }

            await using (enumerator)
            {   //2. we persist user message into the existing conversation
                try
                {
                    userCreatedConversationMessage = await _conversationService.CreateConversationMessageAsync(
                        new CreateConversationMessageRequestDTO()
                        {
                            ConversationId = existingConversation.ConversationId,
                            ClientRequestId = clientRequestId,
                            Role = ConversationMessageRole.User,
                            Body = userQuestion
                        });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to add the conversation message for conversation: {ConversationId}", existingConversation.ConversationId);
                    throw new HubException("There was an error processing your request. Please try again.");
                }

                var hasYieldedAnyContent = false;
                var stringBuilder = new StringBuilder();
                var assistantMessageAccumulated = "";

                if (firstText is not null)
                {
                    assistantMessageAccumulated = BuildAssistantConversationMessage(stringBuilder, firstText);
                    hasYieldedAnyContent = true;
                    yield return firstText;
                }

                while (true)
                {
                    var hasNext = false;
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
                    }

                    if (streamFailed)
                        throw new HubException("The response was interrupted partway through. Please try again.");
                    //3. we persist assistant message into the existing conversation
                    if (!hasNext)
                    {
                        assistantCreatedConversationMessage = await _conversationService.CreateConversationMessageAsync(
                            new CreateConversationMessageRequestDTO()
                            {
                                ConversationId = existingConversation.ConversationId,
                                ClientRequestId = null,
                                Role = ConversationMessageRole.Assistant,
                                Body = assistantMessageAccumulated
                            });

                        yield break;
                    }

                    if (enumerator.Current.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                    {
                        hasYieldedAnyContent = true;
                        assistantMessageAccumulated = BuildAssistantConversationMessage(stringBuilder, text.Text);
                        yield return text.Text;
                    }
                }
            }
        }

        private IReadOnlyList<MessageParam> ToAnthropicMessageParams(List<ConversationMessageDTO> conversationMessages)
        {
            if (conversationMessages.Count == 0) return [];

            return conversationMessages
                .Select(x => new MessageParam()
                {
                    Role = x.Role,
                    Content = x.Content
                }).ToList();
        }

        private static string BuildAssistantConversationMessage(StringBuilder stringBuilder, string assistantMessage)
        {
            stringBuilder.Append(assistantMessage);

            return stringBuilder.ToString();
        }
    }
}
