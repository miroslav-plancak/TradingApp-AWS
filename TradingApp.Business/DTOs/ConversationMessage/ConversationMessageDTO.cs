using System;
using TradingApp.Domain.Models.Enums;

namespace TradingApp.Business.DTOs.ConversationMessage
{
    public class ConversationMessageDTO
    {
        public required Guid ConversationId { get; set; }
        public required ConversationMessageRole Role { get; set; }
        public required string Body { get; set; }
        public required DateTimeOffset CreatedAt { get; set; }
    }
}
