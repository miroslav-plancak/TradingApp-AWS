using System;

namespace TradingApp.Domain.Models.Entities.DeadLetterLog
{
    public class DeadLetterLog
    {
        public Guid Id { get; set; }
        public Guid ClientOrderId { get; set; }
        public required string MessageBody { get; set; }
        public required string Reason { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public bool IsResolved { get; set; }
        public required string ResolutionNotes { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }
        public required string ResolvedBy { get; set; }
        public  string? CorrelationId { get; set; }
    }
}