using System;

namespace TradingApp.Domain.Models.Entities.OrderNotificationSequences
{
    public class OrderNotificationSequence
    {
        public Guid ClientOrderId { get; set; }
        public int LastProcessedSequence { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}