using Anthropic.Models.Messages;

namespace TradingApp.Infrastructure.Interfaces
{
    public interface IAnthropicApiService
    {
        Task<string?> DispatchPromptAsync(MessageCreateParams msgParams, string userMessage);
        IAsyncEnumerable<string> EstablishStreamAsync
        (
                 string userMessage,
                 Guid conversationId,
                 bool isNewConversation,
                 MessageCreateParams messageCreateParams,
                 Func<Guid, Task<bool>> deleteConversationHandler,
                 Func<string, Task> persistUserMessageHandler,
                 Func<string, Task> persistAssistantMessageHandler
        );
    }
}
