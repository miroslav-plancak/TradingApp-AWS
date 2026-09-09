using System;

namespace TradingApp.Domain.Models.Entities.ConversationFullFile
{
    public class ConversationFullFile
    {
        public Guid Id { get; set; }
        public required Guid ConversationId { get; set; }
        public required string SourceFile { get; set; }
        public required string Content { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}
