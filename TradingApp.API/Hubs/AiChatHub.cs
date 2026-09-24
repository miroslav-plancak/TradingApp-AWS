using Anthropic.Models.Messages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Conversation;
using TradingApp.Business.DTOs.ConversationMessage;
using TradingApp.Business.Interfaces.Services.Regular.Conversation;
using TradingApp.Domain.Models.Enums;
using TradingApp.Infrastructure.Exceptions;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.ConversationMemory;
using TradingApp.Infrastructure.Interfaces.Retrieval;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.API.Hubs
{
    public class AiChatHub : BaseHub<AiChatHub>
    {
        private readonly IFileDebugLogger _fileDebugLogger;
        private readonly IAnthropicApiService _anthropicApiService;
        private readonly IChunkRetrievalService _chunkRetrievalService;
        private readonly IConversationService _conversationService;
        private readonly IConversationMessageService _conversationMessageService;
        private readonly IConversationSummaryService _conversationSummaryService;
        private readonly IConversationCompactorService _conversationCompactorService;

        public AiChatHub
        (
            ILogger<AiChatHub> logger,
            IFileDebugLogger fileDebugLogger,
            IAnthropicApiService anthropicApiService,
            IChunkRetrievalService chunkRetrievalService,
            IConversationService conversationService,
            IConversationSummaryService conversationSummaryService,
            IConversationMessageService conversationMessageService,
            IConversationCompactorService conversationCompactorService
        ) : base(logger)
        {
            _fileDebugLogger = fileDebugLogger;
            _anthropicApiService = anthropicApiService;
            _chunkRetrievalService = chunkRetrievalService;
            _conversationService = conversationService;
            _conversationMessageService = conversationMessageService;
            _conversationSummaryService = conversationSummaryService;
            _conversationCompactorService = conversationCompactorService;
        }

        public async IAsyncEnumerable<string> SendUserMessage
        (
            string userMessage,
            Guid? conversationId,
            Guid? clientRequestId
        )
        {
            if (string.IsNullOrWhiteSpace(userMessage)) throw new HubException("Message cannot be empty.");

            var (isNewConversation, existingConversation) = await ResolveConversationAsync(conversationId, clientRequestId, userMessage);

            var retrievalResult = await RetrieveAdditionalContextAsync(userMessage, existingConversation.ConversationId);

            var conversationMessagesHistory = await RetrieveConversationHistoryAsync(existingConversation, userMessage);

            AppendUserMessageToHistory(conversationMessagesHistory, userMessage);

            await _fileDebugLogger.LogSectionAsync("0-conversation-history", $"Current user/assistant correspodence:",
                            RetrievalResultLogFormatter.FormatCurrentConversationMessagesIntoFileLog(conversationMessagesHistory));
           
            var parameters = ConfigureMessageParams(retrievalResult, conversationMessagesHistory, existingConversation.CompactedSummary);

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
                async (msgBody) =>
                {
                    await _conversationMessageService.CreateConversationMessageAsync(new CreateConversationMessageRequestDTO
                    {
                        ConversationId = existingConversation.ConversationId,
                        ClientRequestId = clientRequestId,
                        Role = ConversationMessageRole.User,
                        Body = msgBody
                    });
                },
                async (msgBody) =>
                {
                    await _conversationMessageService.CreateConversationMessageAsync(new CreateConversationMessageRequestDTO
                    {
                        ConversationId = existingConversation.ConversationId,
                        ClientRequestId = null,
                        Role = ConversationMessageRole.Assistant,
                        Body = msgBody
                    });
                },
                async (stopReasonResponse) =>
                {
                    await NotifyStopReasonAsync(stopReasonResponse.ToString());
                },
                async (tokenUsage) =>
                {
                    await _conversationCompactorService.CompactConversationAsync(existingConversation.ConversationId, tokenUsage, parameters.MaxTokens);
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
                        if (ex is ChatStreamFailureException { IsRetryable: false })
                        {
                            await NotifyChatStreamFailureAsync(ex.Message);
                        }

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

        private async Task<(bool isNewConversation, ConversationCompactionStateDTO existingConversation)> ResolveConversationAsync
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

                    return (true, new ConversationCompactionStateDTO { ConversationId = existingConversation.ConversationId });
                }
                else
                {
                    try
                    {
                        var conversationCompactionState = await _conversationSummaryService.GetConversationCompactionStateAsync(conversationId.Value);

                        return (false, conversationCompactionState);
                    }
                    catch (KeyNotFoundException)
                    {
                        existingConversation = await _conversationService.CreateConversationAsync(userMessage, clientRequestId);

                        await NotifyConversationStartedAsync(existingConversation.ConversationId);

                        return (true, new ConversationCompactionStateDTO { ConversationId = existingConversation.ConversationId });
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

        private async Task NotifyStopReasonAsync(string stopReason)
        {
            try
            {
                await Clients.Caller.SendAsync("ResponseTruncated", stopReason);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify client of stop reason {StopReason}", stopReason);
            }
        }

        private async Task NotifyChatStreamFailureAsync(string message)
        {
            try
            {
                await Clients.Caller.SendAsync("NonRetryableChatFailure");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify client of chat stream failure: {Mesage}", message);
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

        private async Task<List<ConversationHistoryMessageDTO>> RetrieveConversationHistoryAsync
        (
            ConversationCompactionStateDTO existingConversation,
            string userMessage
        )
        {
            List<ConversationHistoryMessageDTO> conversationMessagesHistory = [];
            try
            {
                conversationMessagesHistory = await _conversationMessageService.GetConversationMessagesAsync(
                    existingConversation.ConversationId, existingConversation.SummaryCoversMessagesUpTo);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure retrieving conversation messages for message: {UserMessage}", userMessage);
            }

            return conversationMessagesHistory;
        }

        private static void AppendUserMessageToHistory
        (
            List<ConversationHistoryMessageDTO> conversationMessagesHistory,
            string userMessage
        )
        {
            conversationMessagesHistory.Add(
                new ConversationHistoryMessageDTO
                {
                    Role = ConversationMessageRole.User.ToString().ToLower(),
                    Content = userMessage
                }
            );
        }

        private static MessageCreateParams ConfigureMessageParams
        (
            RetrievalResult retrievalResult,
            List<ConversationHistoryMessageDTO> conversationMessagesHistory,
            string compactedSummary
        )
        {
            return new MessageCreateParams
            {
                Model = "claude-sonnet-5",
                MaxTokens = 4096,
                System = SystemPromptBuilder.BuildChatSystemPrompt(retrievalResult, compactedSummary),
                Messages = ToAnthropicMessageParams(conversationMessagesHistory)
            };
        }

        private static IReadOnlyList<MessageParam> ToAnthropicMessageParams(List<ConversationHistoryMessageDTO> conversationMessages)
        {
            if (conversationMessages.Count == 0) return [];

            var cacheIndex = conversationMessages.Count - 2;

            return conversationMessages
                .Select((message, index) => new MessageParam()
                {
                    Role = message.Role,
                    Content = (index == cacheIndex) ? MarkLastAssistantMessageAsCacheBreakpoint(message) : message.Content
                }).ToList();
        }

        private static List<ContentBlockParam> MarkLastAssistantMessageAsCacheBreakpoint(ConversationHistoryMessageDTO message)
        {
            return new List<ContentBlockParam>
            {
               new TextBlockParam(message.Content)
               {
                   CacheControl = new CacheControlEphemeral()
               }
            };
        }
    }
}
