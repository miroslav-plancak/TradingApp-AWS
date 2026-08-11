namespace TradingApp.Events.Events
{
    public class IntegrationEvent
    {
        public string CorrelationId { get; set; } = string.Empty;
        public Guid ClientOrderId { get; set; }
        public DateTimeOffset EventTime { get; set; }
    }
}