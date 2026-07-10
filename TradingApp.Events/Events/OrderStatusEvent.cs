namespace TradingApp.Events.Events
{
    public class OrderStatusEvent
    {
        public Guid ClientOrderId { get; set; }
        public required string Status { get; set; }
        public DateTimeOffset EventTime { get; set; }
        public int Sequence { get; set; }
        public string CorrelationId { get; set; } = string.Empty;
    }
}