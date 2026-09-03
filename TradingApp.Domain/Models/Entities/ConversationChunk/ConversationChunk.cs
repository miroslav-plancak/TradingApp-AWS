using System;

namespace TradingApp.Domain.Models.Entities.ConversationChunk
{
    public class ConversationChunk
    {
        public Guid Id { get; set; }
        public required Guid ConversationId { get; set; }
        public required string Key { get; set; }
        public required string SourceFile { get; set; }
        public required string Content { get; set; }
        public required DateTimeOffset CreatedAt { get; set; }
    }
}
