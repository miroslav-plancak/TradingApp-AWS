namespace TradingApp.Events.Events
{
    public class OrderStatusChangedEvent : IntegrationEvent
    {
        public int Sequence { get; set; }
        public required string Status { get; set; }
    }
}
