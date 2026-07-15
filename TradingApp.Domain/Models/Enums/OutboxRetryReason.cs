namespace TradingApp.Domain.Models.Enums
{
    public enum OutboxRetryReason
    {
        None = 0,
        SimpleQueueServiceUnavailable = 1,
        InvalidPayload = 2,
        DatabaseError = 3,
        Unknown = 4
    }
}