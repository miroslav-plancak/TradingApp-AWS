using System;

namespace TradingApp.Business.DTOs.ConversationMessage
{
    public class ConversationHistoryMessageDTO
    {
        public string Role { get; set; }
        public string Content { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}
