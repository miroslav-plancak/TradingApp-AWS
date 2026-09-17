using System;

namespace TradingApp.Business.DTOs.Conversation
{
    public class ConversationCompactionStateDTO
    {
        public Guid ConversationId { get; set; }
        public string CompactedSummary { get; set; }
        public DateTimeOffset? SummaryCoversMessagesUpTo { get; set; }
    }
}
