using System;

namespace TradingApp.Business.DTOs.ConversationChunk
{
    public class CreateConversationChunkRequestDTO
    {
        public required Guid ConversationId { get; set; }
        public required string Key { get; set; }
        public required string SourceFile { get; set; }
        public required string Content { get; set; }
    }
}
