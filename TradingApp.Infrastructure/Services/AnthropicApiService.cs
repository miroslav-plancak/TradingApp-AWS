using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using TradingApp.Infrastructure.Helpers;
using TradingApp.Infrastructure.Interfaces;

namespace TradingApp.Infrastructure.Services
{
    public class AnthropicApiService : IAnthropicApiService
    {
        private readonly ILogger<AnthropicApiService> _logger;
        private readonly AnthropicClient _anthropicClient;
        private readonly IAsyncPolicy _resiliencePolicy;
        private readonly IFileDebugLogger _fileDebugLogger;

        public AnthropicApiService
        (
            ILogger<AnthropicApiService> logger,
            AnthropicClient anthropicClient,
            [FromKeyedServices(ResiliencePolicyKey.AnthropicAPI)] IAsyncPolicy resiliencePolicy,
            IFileDebugLogger fileDebugLogger
        )
        {
            _logger = logger;
            _anthropicClient = anthropicClient;
            _resiliencePolicy = resiliencePolicy;
            _fileDebugLogger = fileDebugLogger;
        }

        public async Task<string?> DispatchPromptAsync(MessageCreateParams msgParams, string userQuery)
        {
            try
            {
                var anthropicMessageResponse = await _resiliencePolicy.ExecuteAsync(async () =>
                {
                    return await _anthropicClient.Messages.Create(msgParams);
                });

                var firstBlock = anthropicMessageResponse.Content.Count > 0 ? anthropicMessageResponse.Content[0] : null;

                if (firstBlock is not null && firstBlock.TryPickText(out var textblock))
                {
                    if (!string.IsNullOrWhiteSpace(textblock.Text))
                    {
                        return textblock.Text.Trim();
                    }

                    return null;
                }

                return null;
            }
            catch (Exception ex) when (ResiliencePolicyBuilder.IsTransientAnthropicApiException(ex))
            {
                _logger.LogWarning(ex, "AnthropicPromptFailedAfterRetries | Question: {UserQuery}", userQuery);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AnthropicPromptUnexpectedFailure | Question: {UserQuery}", userQuery);
                return null;
            }
        }
    }
}
