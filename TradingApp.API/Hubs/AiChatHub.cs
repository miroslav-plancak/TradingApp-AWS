using Anthropic.Models.Messages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Conversation;
using TradingApp.Business.DTOs.ConversationMessage;
using TradingApp.Business.Interfaces.Services.Regular.Conversation;
using TradingApp.Domain.Models.Enums;
using TradingApp.Infrastructure.Exceptions;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Interfaces.Agentic;
using TradingApp.Infrastructure.Interfaces.ConversationMemory;
using TradingApp.Infrastructure.Models.Agentic;

namespace TradingApp.API.Hubs
{
    public class AiChatHub : BaseHub<AiChatHub>
    {
        private readonly IFileDebugLogger _fileDebugLogger;
        private readonly IConversationService _conversationService;
        private readonly IConversationMessageService _conversationMessageService;
        private readonly IConversationSummaryService _conversationSummaryService;
        private readonly IConversationCompactorService _conversationCompactorService;
        private readonly IAgenticLoopService _agenticLoopService;

        public AiChatHub
        (
            ILogger<AiChatHub> logger,
            IFileDebugLogger fileDebugLogger,
            IConversationService conversationService,
            IConversationSummaryService conversationSummaryService,
            IConversationMessageService conversationMessageService,
            IConversationCompactorService conversationCompactorService,
            IAgenticLoopService agenticLoopService
        ) : base(logger)
        {
            _fileDebugLogger = fileDebugLogger;
            _conversationService = conversationService;
            _conversationMessageService = conversationMessageService;
            _conversationSummaryService = conversationSummaryService;
            _conversationCompactorService = conversationCompactorService;
            _agenticLoopService = agenticLoopService;
        }

        public async IAsyncEnumerable<string> SendUserMessageAsync
        (
             string userMessage,
             Guid? conversationId,
             Guid? clientRequestId
        )
        {
            if (string.IsNullOrWhiteSpace(userMessage)) throw new HubException("Message cannot be empty.");

            var (isNewConversation, existingConversation) = await ResolveConversationAsync(conversationId, clientRequestId, userMessage);

            var conversationMessagesHistory = await RetrieveConversationHistoryAsync(existingConversation, userMessage);

            AppendUserMessageToHistory(conversationMessagesHistory, userMessage);

            await _fileDebugLogger.LogSectionAsync("0-conversation-history", $"Current user/assistant correspodence:",
                            RetrievalResultLogFormatter.FormatCurrentConversationMessagesIntoFileLog(conversationMessagesHistory));

            var conversationHistory = ToAnthropicMessageParams(conversationMessagesHistory);

            long maxTokens = 0;
            MessageDeltaUsage usage = null;
            var responseAccumulator = new StringBuilder();
            int toolUseCounter = 0;

            var enumerator = _agenticLoopService.RunAgenticLoopAsync(new AgenticLoopRequest
            {
                UserMessage = userMessage,
                ConversationId = existingConversation.ConversationId,
                ConversationMessagesHistory = conversationHistory,
                CompactedSummary = existingConversation.CompactedSummary,
                ToolCalledHandler = async (result) =>
                {
                    await Clients.Caller.SendAsync("ToolCalled", $"# {Interlocked.Increment(ref toolUseCounter)} - {result}");
                },
                MaxTokensHandler = (result) => { maxTokens = result; return Task.CompletedTask; },
                UsageHandler = (result) => { usage = result; return Task.CompletedTask; },
                ResponseTruncatedHandler = async (result) => { await Clients.Caller.SendAsync("ResponseTruncated", result); },
                UnexpectedStopReasonHandler = async (result) => { await NotifyStopReasonAsync(result.ToString()); }
            }).GetAsyncEnumerator();

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

                        if (isNewConversation)
                        {
                            await _conversationService.DeleteConversationByIdAsync(existingConversation.ConversationId);
                            await Clients.Caller.SendAsync("ConversationRolledBack", existingConversation.ConversationId);
                        }

                        _logger.LogError(ex, "General error occured while itterating through anthropic streaming response.");

                        throw new HubException(ex.Message);
                    }

                    if (hasNext)
                    {
                        responseAccumulator.Append(enumerator.Current);
                        yield return enumerator.Current;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(responseAccumulator.ToString()))
            {
                if (isNewConversation)
                {
                    await _conversationService.DeleteConversationByIdAsync(existingConversation.ConversationId);
                }

                _logger.LogError("Assistant message response is empty.");
                throw new HubException("Assistant message response failed, try again.");
            }

            try
            {
                await _conversationMessageService.CreateConversationMessageAsync(new CreateConversationMessageRequestDTO
                {
                    ConversationId = existingConversation.ConversationId,
                    ClientRequestId = clientRequestId,
                    Role = ConversationMessageRole.User,
                    Body = userMessage
                });

                await _conversationMessageService.CreateConversationMessageAsync(new CreateConversationMessageRequestDTO
                {
                    ConversationId = existingConversation.ConversationId,
                    ClientRequestId = null,
                    Role = ConversationMessageRole.Assistant,
                    Body = responseAccumulator.ToString()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to persist conversation messages for conversation {ConversationId} - the answer was generated successfully but may be missing from history.",
                    existingConversation.ConversationId);
            }

            if (usage != null)
            {
                await _conversationCompactorService.CompactConversationAsync(existingConversation.ConversationId, usage, maxTokens);
            }
            else
            {
                _logger.LogWarning("SendUserMessageAsyncMissingTokenUsage | ConversationId:{ConversationId}", existingConversation.ConversationId);
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

        private static IReadOnlyList<MessageParam> ToAnthropicMessageParams(List<ConversationHistoryMessageDTO> conversationMessages)
        {
            if (conversationMessages.Count == 0) return [];

            var cacheIndex = conversationMessages.Count - 2;

            const bool enablePromptCaching = false; //NOTE: disabled for reducing the cost of testing.

            return conversationMessages
                .Select((message, index) => new MessageParam()
                {
                    Role = message.Role,
                    Content = (enablePromptCaching && index == cacheIndex) ? MarkLastAssistantMessageAsCacheBreakpoint(message) : message.Content
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
