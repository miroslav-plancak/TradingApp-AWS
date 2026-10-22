namespace TradingApp.Infrastructure.Interfaces.ConversationMemory
{
    public interface IConversationFullDeletionService
    {
        Task<bool> DeleteConversationAndAllItsEntitiesAsync(Guid conversationId);
    }
}
