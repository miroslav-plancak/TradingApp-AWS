namespace TradingApp.Events.Events
{
    public class OutboxMessageProcessedEvent : IntegrationEvent
    {
        public bool IsAlreadyProcessed { get; set; }
    }
}
