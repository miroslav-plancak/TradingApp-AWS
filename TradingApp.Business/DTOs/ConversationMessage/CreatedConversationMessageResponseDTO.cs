using TradingApp.Domain.Models.Enums;

namespace TradingApp.Business.DTOs.ConversationMessage
{
    public class CreatedConversationMessageResponseDTO
    {
        public required ConversationMessageRole Role { get; set; }
        public required string Body { get; set; }
    }
}
