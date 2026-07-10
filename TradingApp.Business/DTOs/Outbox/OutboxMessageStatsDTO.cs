namespace TradingApp.Business.DTOs.Outbox
{
    public class OutboxMessageStatsDTO
    {
        public int TotalCount { get; set; }
        public int ProcessedCount { get; set; }
        public int UnprocessedCount { get; set; }
        public int Last24Hours { get; set; }
    }
}
