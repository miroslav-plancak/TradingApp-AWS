using System;

namespace TradingApp.Business.DTOs.ConversationFullFile
{
    public class CreateConversationFullFileRequestDTO
    {
        public required Guid ConversationId { get; set; }
        public required string SourceFile { get; set; }
        public required string Content { get; set; }
    }
}
