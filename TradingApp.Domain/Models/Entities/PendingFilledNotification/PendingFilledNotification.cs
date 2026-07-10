using System;

namespace TradingApp.Domain.Models.Entities.PendingFilledNotification
{
    public class PendingFilledNotification
    {
        public Guid ClientOrderId { get; set; }
        public string EventPayload { get; set; } = string.Empty; 
        public string CorrelationId { get; set; } = string.Empty;
        public DateTimeOffset StoredAt { get; set; }
    }
}
