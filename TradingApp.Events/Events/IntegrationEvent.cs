namespace TradingApp.Events.Events
{
    public class IntegrationEvent
    {
        public Guid ClientOrderId { get; set; }
        public DateTimeOffset EventTime { get; set; }
        public string CorrelationId { get; set; } = string.Empty;
    }
}