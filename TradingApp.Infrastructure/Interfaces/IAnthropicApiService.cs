using Anthropic.Models.Messages;

namespace TradingApp.Infrastructure.Interfaces
{
    public interface IAnthropicApiService
    {
        Task<string?> DispatchPromptAsync(MessageCreateParams msgParams, string userMessage);
    }
}
