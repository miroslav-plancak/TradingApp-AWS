using System;
using TradingApp.Domain.Models.Enums;

namespace TradingApp.Business.DTOs.ConversationMessage
{
    public class CreateConversationMessageRequestDTO
    {
        public Guid ConversationId { get; set; }
        public Guid? ClientRequestId { get; set; }
        public ConversationMessageRole Role { get; set; }
        public string Body { get; set; }
    }
}
