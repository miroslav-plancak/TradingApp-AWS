using System;
using TradingApp.Domain.Models.Enums;

namespace TradingApp.Domain.Models.Entities.OutboxMessage
{
    public class OutboxMessage
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = default!;
        public string Payload { get; set; } = default!;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ProcessedAt { get; set; }
        public int RetryCount { get; set; }
        public OutboxRetryReason RetryReason { get; set; } = OutboxRetryReason.None;
        public string? LastError { get; set; }
        public string CorrelationId { get; set; } = string.Empty;
    }
}
