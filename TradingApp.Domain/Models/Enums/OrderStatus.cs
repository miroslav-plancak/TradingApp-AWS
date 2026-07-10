namespace TradingApp.Domain.Models.Enums
{
    public enum OrderStatus
    {
        PENDING_ACK = 0,
        ACKNOWLEDGED = 1,
        REJECTED = 2,
        FILLED = 3,
    }
}
