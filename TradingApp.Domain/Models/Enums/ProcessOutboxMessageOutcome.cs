namespace TradingApp.Domain.Models.Enums
{
    public enum ProcessOutboxMessageOutcome
    {
        Sent,
        AlreadyProcessed,
        Failure,
        CircuitOpen
    }
}
