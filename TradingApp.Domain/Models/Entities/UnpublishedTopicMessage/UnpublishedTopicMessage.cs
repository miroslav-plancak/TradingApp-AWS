using System;

namespace TradingApp.Domain.Models.Entities.UnpublishedTopicMessages
{
    public class UnpublishedTopicMessage
    {
        public Guid Id { get; set; }
        public Guid ClientOrderId { get; set; }
        public string EventType { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public int RetryCount { get; set; }
        public string? LastError { get; set; }
        public string CorrelationId { get; set; } = string.Empty;
        public string? ClaimedBy { get; set; }
        public DateTimeOffset? ClaimedAt { get; set; }
    }
}