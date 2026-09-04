using System;

namespace TradingApp.Business.DTOs.Conversation
{
    public class CreatedConversationResponseDTO
    {
        public Guid ConversationId { get; set; }
        public required string Name { get; set; }
        public required DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
    }
}
