namespace TradingApp.Business.DTOs.DeadLetter
{
    public class DeadLetterStatsDTO
    {
        public int TotalCount { get; set; }
        public int UnresolvedCount { get; set; }
        public int ResolvedCount { get; set; }
        public int Last24Hours { get; set; }
    }
}
