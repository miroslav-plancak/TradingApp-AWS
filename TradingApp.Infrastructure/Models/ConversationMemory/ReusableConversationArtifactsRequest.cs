namespace TradingApp.Infrastructure.Models.ConversationMemory
{
    public class ReusableConversationArtifactsRequest
    {
        public Guid ConversationId { get; set; }
        public string UserMessage { get; set; } = string.Empty;
        public bool IsNewConversation { get; set; }
    }
}
