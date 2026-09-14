using Anthropic.Models.Messages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Conversation;
using TradingApp.Business.DTOs.ConversationMessage;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Domain.Models.Enums;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.Retrieval;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.API.Hubs
{
    public class AiChatHub : Hub
    {
        private readonly ILogger<AiChatHub> _logger;
        private readonly IFileDebugLogger _fileDebugLogger;
        private readonly IChunkRetrievalService _chunkRetrievalService;
        private readonly IConversationService _conversationService;
        private readonly IAnthropicApiService _anthropicApiService;

        public AiChatHub
        (
            ILogger<AiChatHub> logger,
            IFileDebugLogger fileDebugLogger,
            IChunkRetrievalService chunkRetrievalService,
            IConversationService conversationService,
            IAnthropicApiService anthropicApiService
        )
        {
            _logger = logger;
            _fileDebugLogger = fileDebugLogger;
            _chunkRetrievalService = chunkRetrievalService;
            _conversationService = conversationService;
            _anthropicApiService = anthropicApiService;
        }

        public override Task OnConnectedAsync()
        {
            _logger.LogInformation("AiChatHub client connected | ConnectionId: {ConnectionId}", Context.ConnectionId);

            return base.OnConnectedAsync();
        }

        public async IAsyncEnumerable<string> SendUserMessage
        (
            string userMessage,
            Guid? conversationId,
            Guid? clientRequestId
        )
        {
            if (string.IsNullOrWhiteSpace(userMessage))
                throw new HubException("Message cannot be empty.");

            var (isNewConversation, existingConversation) = await ResolveConversationAsync(conversationId, clientRequestId, userMessage);

            var retrievalResult = await RetrieveAdditionalContextAsync(userMessage, existingConversation.ConversationId);

            var conversationMessagesHistory = await RetrieveConversationHistoryAsync(existingConversation.ConversationId, userMessage);

            AppendUserMessageToHistory(conversationMessagesHistory, userMessage);

            await _fileDebugLogger.LogSectionAsync("0-conversation-history", $"Current user/assistant correspodence:",
                            RetrievalResultLogFormatter.FormatCurrentConversationMessagesIntoFileLog(conversationMessagesHistory));

            var parameters = ConfigureMessageParams(retrievalResult, conversationMessagesHistory);

            IAsyncEnumerator<string> enumerator = null;

            enumerator = _anthropicApiService.EstablishStreamAsync
            (
                userMessage,
                existingConversation.ConversationId,
                isNewConversation,
                parameters,
                async (convId) =>
                {
                    return await _conversationService.DeleteConversationByIdAsync(convId);
                },
                async (body) =>
                {
                    await _conversationService.CreateConversationMessageAsync(new CreateConversationMessageRequestDTO
                    {
                        ConversationId = existingConversation.ConversationId,
                        ClientRequestId = clientRequestId,
                        Role = ConversationMessageRole.User,
                        Body = body
                    });
                },
                async (body) =>
                {
                    await _conversationService.CreateConversationMessageAsync(new CreateConversationMessageRequestDTO
                    {
                        ConversationId = existingConversation.ConversationId,
                        ClientRequestId = null,
                        Role = ConversationMessageRole.Assistant,
                        Body = body
                    });
                }
            ).GetAsyncEnumerator();

            await using (enumerator)
            {
                while (true)
                {
                    var hasNext = false;

                    try
                    {
                        hasNext = await enumerator.MoveNextAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "General error occured while itterating through anthropic streaming response.");
                        throw new HubException(ex.Message);
                    }

                    if (hasNext)
                    {
                        yield return enumerator.Current;
                    }
                    else
                    {
                        yield break;
                    }
                }
            }
        }

        private async Task<(bool isNewConversation, CreatedConversationResponseDTO existingConversation)> ResolveConversationAsync
        (
            Guid? conversationId,
            Guid? clientRequestId,
            string userMessage
        )
        {
            CreatedConversationResponseDTO existingConversation;

            try
            {
                if (conversationId == null)
                {
                    existingConversation = await _conversationService.CreateConversationAsync(userMessage, clientRequestId);

                    await NotifyConversationStartedAsync(existingConversation.ConversationId);

                    return (true, existingConversation);
                }
                else
                {
                    try
                    {
                        existingConversation = await _conversationService.GetConversationByIdAsync(conversationId.Value);

                        return (false, existingConversation);
                    }
                    catch (KeyNotFoundException)
                    {
                        existingConversation = await _conversationService.CreateConversationAsync(userMessage, clientRequestId);

                        await NotifyConversationStartedAsync(existingConversation.ConversationId);

                        return (true, existingConversation);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to establish conversation context for message: {UserMessage}", userMessage);
                throw new HubException("There was an error processing your request. Please try again.");
            }
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

        private async Task<RetrievalResult> RetrieveAdditionalContextAsync
        (
            string userMessage,
            Guid existingConversationId
        )
        {
            var retrievalResult = new RetrievalResult { ChunkFallbacks = [], FullFileContents = [] };

            try
            {
                retrievalResult = await _chunkRetrievalService.RetrieveRelevantContextAsync(userMessage, existingConversationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure retrieving context for message: {UserMessage}", userMessage);
            }

            return retrievalResult;
        }

        private async Task<List<ConversationMessageDTO>> RetrieveConversationHistoryAsync
        (
            Guid existingConversationId,
            string userMessage
        )
        {
            List<ConversationMessageDTO> conversationMessagesHistory = [];
            try
            {
                conversationMessagesHistory = await _conversationService.GetConversationMessagesAsync(existingConversationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure retrieving conversation messages for message: {UserMessage}", userMessage);
            }

            return conversationMessagesHistory;
        }

        private static void AppendUserMessageToHistory
        (
            List<ConversationMessageDTO> conversationMessagesHistory,
            string userMessage
        )
        {
            conversationMessagesHistory.Add(
                new ConversationMessageDTO
                {
                    Role = ConversationMessageRole.User.ToString().ToLower(),
                    Content = userMessage
                }
            );
        }

        private static MessageCreateParams ConfigureMessageParams
        (
            RetrievalResult retrievalResult,
            List<ConversationMessageDTO> conversationMessagesHistory
        )
        {
            return new MessageCreateParams
            {
                Model = "claude-sonnet-5",
                MaxTokens = 4096,
                System = SystemPromptBuilder.BuildSystemPrompt(retrievalResult),
                Messages = ToAnthropicMessageParams(conversationMessagesHistory)
            };
        }

        private static IReadOnlyList<MessageParam> ToAnthropicMessageParams(List<ConversationMessageDTO> conversationMessages)
        {
            if (conversationMessages.Count == 0) return [];

            return conversationMessages
                .Select(x => new MessageParam()
                {
                    Role = x.Role,
                    Content = x.Content
                }).ToList();
        }
    }
}
