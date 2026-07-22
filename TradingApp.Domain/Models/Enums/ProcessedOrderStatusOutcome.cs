namespace TradingApp.Domain.Models.Enums
{
    public enum ProcessedOrderStatusOutcome
    {
        Filled, 
        FilledPublishDeferred,
        SaveFailed, 
        CircuitOpen
    }
}
