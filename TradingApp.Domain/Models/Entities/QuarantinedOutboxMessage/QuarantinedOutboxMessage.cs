using System;
using TradingApp.Domain.Models.Enums;

namespace TradingApp.Domain.Models.Entities.QuarantinedOutboxMessage
{
    public class QuarantinedOutboxMessage
    {
        public Guid Id { get; set; }
        public Guid OriginalOutboxMessageId { get; set; }
        public Guid? ClientOrderId { get; set; }
        public string Payload { get; set; } = default!;
        public OutboxRetryReason Reason { get; set; }
        public int FinalRetryCount { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTimeOffset QuarantinedAt { get; set; }
        public bool IsResurrected { get; set; } = false;
        public DateTimeOffset? ResurrectedAt { get; set; }
        public bool IsDiscarded { get; set; } = false;
        public DateTimeOffset? DiscardedAt { get; set; }
        public string? DiscardedBy { get; set; }
        public string? ResolutionNotes { get; set; }
        public string CorrelationId { get; set; } = string.Empty;
    }
}
