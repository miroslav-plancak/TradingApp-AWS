using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using System.Runtime.CompilerServices;
using TradingApp.Infrastructure.Exceptions;
using TradingApp.Infrastructure.Helpers;
using TradingApp.Infrastructure.Helpers.Anthropic;
using TradingApp.Infrastructure.Interfaces.Anthropic;

namespace TradingApp.Infrastructure.Services.Anthropic
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

        public async Task<Message?> DispatchPromptWithFullResponseAsync(MessageCreateParams msgParams, string? userMessage, CancellationToken ct)
        {
            try
            {
                var anthropicMessageResponse = await _resiliencePolicy.ExecuteAsync(async (policyCancellationToken) =>
                {
                    return await _anthropicClient.Messages.Create(msgParams, policyCancellationToken);
                },ct);

                if (anthropicMessageResponse.Content.Count == 0)
                {
                    _logger.LogError("AnthropicPromptReturnedNoContent | UserMessage: {UserMessage}", userMessage);
                }

                return anthropicMessageResponse;
            }
            catch (OperationCanceledException osEx) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation(osEx, "AnthropicPromptCancelled | UserMessage: {UserMessage}", userMessage);
                throw;
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

        public async Task<string?> DispatchPromptAsync(MessageCreateParams msgParams, string? userMessage, CancellationToken ct)
        {
            var anthropicMessageResponse = await DispatchPromptWithFullResponseAsync(msgParams, userMessage, ct); 

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

        public async IAsyncEnumerable<string> EstablishAgenticLoopStreamAsync
        (
            string userMessage,
            MessageCreateParams messageCreateParams,
            Func<StopReason, Task> stopReasonHandler,
            Func<MessageDeltaUsage, Task> usageHandler,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            IAsyncEnumerator<RawMessageStreamEvent>? enumerator = null;
            string? firstText = null;
            StopReason? bootstrapStopReason = null;
            MessageDeltaUsage? bootstrapUsage = null;
            var bootstrapFailed = false;
            string? failureMessage = null;
            var isRetryable = false;

            try
            {
                (enumerator, firstText, bootstrapStopReason, bootstrapUsage) = await _resiliencePolicy.ExecuteAsync(async (policyCancellationToken) =>
                {
                    var e = _anthropicClient.Messages.CreateStreaming(messageCreateParams, policyCancellationToken)
                                    .GetAsyncEnumerator(policyCancellationToken);
                    StopReason? capturedStopReason = null;
                    MessageDeltaUsage? capturedUsage = null;

                    try
                    {
                        while (await e.MoveNextAsync())
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                _logger.LogInformation("CreateStreamingCancelled | UserMessage: {UserMessage}", userMessage);
                                cancellationToken.ThrowIfCancellationRequested();
                            }

                            if (e.Current.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                            {
                                return (e, text.Text, capturedStopReason, capturedUsage);
                            }

                            if (e.Current.TryPickDelta(out var messageDelta) && messageDelta?.Delta?.StopReason?.Value() != null)
                            {
                                capturedUsage = messageDelta.Usage;
                                capturedStopReason = messageDelta.Delta.StopReason.Value();
                            }
                        }

                        return (e, (string?)null, capturedStopReason, capturedUsage);
                    }
                    catch (OperationCanceledException ocEx) when (cancellationToken.IsCancellationRequested)
                    {
                        await e.DisposeAsync();
                        _logger.LogInformation(ocEx, "CreateStreamingBootstrapAttemptCancelled | UserMessage: {UserMessage}", userMessage);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        await e.DisposeAsync();
                        _logger.LogError(ex, "Streaming bootstrap attempt failed before reaching first content/stop-reason: {UserMessage}", userMessage);
                        throw;
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException ocEx) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(ocEx, "CreateStreamingBootstrapCancelled | UserMessage: {UserMessage}", userMessage);
                throw;
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
                throw new ChatStreamFailureException(failureMessage ?? "There was an error processing your request. Please try again.", isRetryable);
            }

            await using (enumerator)
            {
                var hasYieldedAnyContent = false;
                StopReason stopReason = StopReason.EndTurn;

                if (firstText is not null)
                {
                    hasYieldedAnyContent = true;
                    yield return firstText;
                }
                else if (bootstrapStopReason is not null)
                {
                    if (bootstrapUsage is not null)
                    {
                        await usageHandler(bootstrapUsage);
                    }

                    stopReason = bootstrapStopReason.Value;

                    if (AnthropicStopReasonEvaluator.IsNotCoveredStopReason(stopReason) || AnthropicStopReasonEvaluator.IsCoveredStopReason(stopReason))
                    {
                        await stopReasonHandler(stopReason);
                    }

                    yield break;
                }

                while (true)
                {
                    var hasNext = false;
                    var streamFailed = false;
                    var midStreamIsRetryable = false;

                    string? midStreamFailureMessage = null;

                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        hasNext = await enumerator.MoveNextAsync();
                    }
                    catch (OperationCanceledException ocEx) when (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation(ocEx, "CreateStreamingEnumeratorCancelled | UserMessage: {UserMessage}", userMessage);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Streaming failure while answering message: {UserMessage} | AnyContentYielded: {HasYieldedAnyContent}",
                        userMessage, hasYieldedAnyContent);
                        streamFailed = true;
                        midStreamIsRetryable = ResiliencePolicyBuilder.IsTransientAnthropicApiException(ex);
                        midStreamFailureMessage = BuildFailureMessage(ex);
                    }

                    if (streamFailed)
                        throw new ChatStreamFailureException(midStreamFailureMessage ?? "The response was interrupted partway through. Please try again.", midStreamIsRetryable);

                    if (!hasNext)
                    {
                        if (AnthropicStopReasonEvaluator.IsNotCoveredStopReason(stopReason))
                        {
                            await stopReasonHandler(stopReason);
                        }

                        yield break;
                    }

                    if (enumerator.Current.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                    {
                        hasYieldedAnyContent = true;
                        yield return text.Text;
                    }

                    if (enumerator.Current.TryPickDelta(out var messageDelta) && messageDelta?.Delta?.StopReason?.Value() != null)
                    {
                        await usageHandler(messageDelta.Usage);

                        stopReason = messageDelta.Delta.StopReason.Value();

                        if (AnthropicStopReasonEvaluator.IsCoveredStopReason(stopReason))
                        {
                            await stopReasonHandler(stopReason);
                        }
                    }
                }
            }
        }

        private static string BuildFailureMessage(Exception ex)
        {
            return ex is AnthropicApiException apiEx
              ? AnthropicErrorMessageParser.ExtractMessage(apiEx.ResponseBody)
              : ex.Message;
        }
    }
}
