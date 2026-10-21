using Anthropic.Models.Messages;

namespace TradingApp.Infrastructure.Interfaces.Anthropic
{
    public interface IAnthropicApiService
    {
        Task<Message?> DispatchPromptWithFullResponseAsync
        (
            MessageCreateParams msgParams, 
            string? userMessage = "", 
            CancellationToken ct  = default
        );
        Task<string?> DispatchPromptAsync
        (
            MessageCreateParams msgParams, 
            string? userMessage = "", 
            CancellationToken ct  = default
        );
        IAsyncEnumerable<string> EstablishAgenticLoopStreamAsync
        (
         string userMessage,
         MessageCreateParams messageCreateParams,
         Func<StopReason, Task> stopReasonHandler,
         Func<MessageDeltaUsage, Task> usageHandler,
         CancellationToken ct
        );
    }
}
