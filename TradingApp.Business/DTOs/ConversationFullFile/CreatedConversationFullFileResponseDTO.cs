using System;

namespace TradingApp.Business.DTOs.ConversationFullFile
{
    public class CreatedConversationFullFileResponseDTO
    {
        public required Guid ConversationId { get; set; }
        public required string SourceFile { get; set; }
        public required string Content { get; set; }
    }
}
