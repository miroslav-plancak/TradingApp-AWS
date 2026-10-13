using Anthropic.Models.Messages;

namespace TradingApp.Infrastructure.Interfaces.Anthropic
{
    public interface IAnthropicApiService
    {
        Task<Message?> DispatchPromptWithFullResponseAsync(MessageCreateParams msgParams, string? userMessage = "");
        Task<string?> DispatchPromptAsync(MessageCreateParams msgParams, string? userMessage = "");
        IAsyncEnumerable<string> EstablishAgenticLoopStreamAsync
        (
         string userMessage,
         MessageCreateParams messageCreateParams,
         Func<StopReason, Task> stopReasonHandler,
         Func<MessageDeltaUsage, Task> usageHandler
        );
    }
}
