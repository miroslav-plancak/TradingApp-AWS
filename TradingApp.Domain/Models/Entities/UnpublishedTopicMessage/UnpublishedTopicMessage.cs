using System;
using TradingApp.Domain.Models.Enums;

namespace TradingApp.Domain.Models.Entities.UnpublishedTopicMessages
{
    public class UnpublishedTopicMessage
    {
        public Guid Id { get; set; }
        public Guid ClientOrderId { get; set; }
        public OrderStatus OrderStatus { get; set; }
        public DateTimeOffset ProcessedAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public int RetryCount { get; set; }
        public string? LastError { get; set; }
        public string CorrelationId { get; set; } = string.Empty;
    }
}
