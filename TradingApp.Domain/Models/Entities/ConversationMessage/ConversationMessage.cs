using System;
using TradingApp.Domain.Models.Enums;

namespace TradingApp.Domain.Models.Entities.ConversationMessage
{
    public class ConversationMessage
    {
        public Guid Id { get; set; }
        public required Guid ConversationId { get; set; }
        public required ConversationMessageRole Role { get; set; }
        public required string Body { get; set; }
        public required DateTimeOffset CreatedAt { get; set; }
    }
}
