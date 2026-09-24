using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using System.Text;
using TradingApp.Infrastructure.Exceptions;
using TradingApp.Infrastructure.Helpers;
using TradingApp.Infrastructure.Interfaces;

namespace TradingApp.Infrastructure.Services
{
    public class AnthropicApiService : IAnthropicApiService
    {
        private readonly ILogger<AnthropicApiService> _logger;
        private readonly AnthropicClient _anthropicClient;
        private readonly IAsyncPolicy _resiliencePolicy;

        public AnthropicApiService
        (
            ILogger<AnthropicApiService> logger,
            AnthropicClient anthropicClient,
            [FromKeyedServices(ResiliencePolicyKey.AnthropicAPI)] IAsyncPolicy resiliencePolicy
        )
        {
            _logger = logger;
            _anthropicClient = anthropicClient;
            _resiliencePolicy = resiliencePolicy;
        }

        public async Task<Message?> DispatchPromptWithFullResponseAsync(MessageCreateParams msgParams, string? userMessage)
        {
            try
            {
                var anthropicMessageResponse = await _resiliencePolicy.ExecuteAsync(async () =>
                {
                    return await _anthropicClient.Messages.Create(msgParams);
                });

                if (anthropicMessageResponse.Content.Count == 0)
                {
                    _logger.LogError("AnthropicPromptReturnedNoContent | UserMessage: {UserMessage}", userMessage);
                }

                return anthropicMessageResponse;
            }
            catch (Exception ex) when (ResiliencePolicyBuilder.IsTransientAnthropicApiException(ex))
            {
                _logger.LogWarning(ex, "AnthropicPromptFailedAfterRetries | UserMessage: {UserMessage}", userMessage);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AnthropicPromptUnexpectedFailure | UserMessage: {UserMessage}", userMessage);
                return null;
            }
        }

        public async Task<string?> DispatchPromptAsync(MessageCreateParams msgParams, string? userMessage)
        {
            var anthropicMessageResponse = await DispatchPromptWithFullResponseAsync(msgParams, userMessage);

            if (anthropicMessageResponse == null) return null;

            foreach (var block in anthropicMessageResponse.Content)
            {
                if (block.TryPickText(out var textBlock) && !string.IsNullOrWhiteSpace(textBlock.Text))
                {
                    return textBlock.Text.Trim();
                }
            }

            return null;
        }

        public async IAsyncEnumerable<string> EstablishStreamAsync
        (
                 string userMessage,
                 Guid conversationId,
                 bool isNewConversation,
                 MessageCreateParams messageCreateParams,
                 Func<Guid, Task<bool>> deleteConversationHandler,
                 Func<string, Task> persistUserMessageHandler,
                 Func<string, Task> persistAssistantMessageHandler,
                 Func<StopReason, Task> stopReasonHandler,
                 Func<MessageDeltaUsage, Task> usageHandler
        )
        {
            IAsyncEnumerator<RawMessageStreamEvent>? enumerator = null;
            string? firstText = null;
            var bootstrapFailed = false;
            string? failureMessage = null;
            var isRetryable = false;

            try
            {
                (enumerator, firstText) = await _resiliencePolicy.ExecuteAsync(async () =>
                {
                    var e = _anthropicClient.Messages.CreateStreaming(messageCreateParams).GetAsyncEnumerator();

                    while (await e.MoveNextAsync())
                    {
                        if (e.Current.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                        {
                            return (e, text.Text);
                        }
                    }

                    return (e, (string?)null);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Streaming failure before any content was produced for message: {UserMessage}", userMessage);
                bootstrapFailed = true;
                failureMessage = BuildFailureMessage(ex);
                isRetryable = ResiliencePolicyBuilder.IsTransientAnthropicApiException(ex);
            }

            if (bootstrapFailed || enumerator is null)
            {
                if (isNewConversation)
                {
                    try
                    {
                        await deleteConversationHandler(conversationId);
                    }
                    catch (Exception deleteEx)
                    {
                        _logger.LogError(deleteEx, "Failed to clean up orphaned conversation {ConversationId} after bootstrap failure", conversationId);
                    }
                }

                throw new ChatStreamFailureException(failureMessage ?? "There was an error processing your request. Please try again.", isRetryable);
            }

            await using (enumerator)
            {
                try
                {
                    await persistUserMessageHandler(userMessage);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to add the conversation message for conversation: {ConversationId}", conversationId);
                    throw new InvalidOperationException("There was an error processing your request. Please try again.");
                }

                var hasYieldedAnyContent = false;
                var stringBuilder = new StringBuilder();
                var assistantMessageAccumulated = "";
                StopReason stopReason = StopReason.EndTurn;

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
                        _logger.LogError(ex, "Streaming failure while answering message: {UserMessage} | AnyContentYielded: {HasYieldedAnyContent}",
                            userMessage, hasYieldedAnyContent);
                        streamFailed = true;
                    }

                    if (streamFailed)
                        throw new InvalidOperationException("The response was interrupted partway through. Please try again.");

                    if (!hasNext)
                    {
                        await persistAssistantMessageHandler(assistantMessageAccumulated);

                        if (IsNotableStopReason(stopReason))
                        {
                            await stopReasonHandler(stopReason);
                        }

                        yield break;
                    }

                    if (enumerator.Current.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                    {
                        hasYieldedAnyContent = true;
                        assistantMessageAccumulated = BuildAssistantConversationMessage(stringBuilder, text.Text);
                        yield return text.Text;
                    }

                    if (enumerator.Current.TryPickDelta(out var messageDelta) && messageDelta?.Delta?.StopReason?.Value() != null)
                    {
                        //_logger.LogInformation("Usage:{Usage}", messageDelta.Usage);
                        await usageHandler(messageDelta.Usage);

                        stopReason = messageDelta.Delta.StopReason.Value();
                    }
                }
            }
        }

        private static bool IsNotableStopReason(StopReason stopReason)
        {
            switch (stopReason)
            {
                case StopReason.EndTurn:
                    return false;
                case StopReason.PauseTurn:
                    return false;
                case StopReason.StopSequence:
                    return false;
                case StopReason.ToolUse:
                    return false;
                case StopReason.Refusal:
                    return true;
                case StopReason.ModelContextWindowExceeded:
                    return true;
                case StopReason.MaxTokens:
                    return true;
                default:
                    return false;
            }
        }

        private static string BuildAssistantConversationMessage
        (
           StringBuilder stringBuilder,
           string assistantMessage
        )
        {
            stringBuilder.Append(assistantMessage);
            return stringBuilder.ToString();
        }

        private static string BuildFailureMessage(Exception ex)
        {
            return ex is AnthropicApiException apiEx
              ? AnthropicErrorMessageParser.ExtractMessage(apiEx.ResponseBody)
              : ex.Message;
        }
    }
}
