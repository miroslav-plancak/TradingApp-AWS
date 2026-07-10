namespace TradingApp.Business.DTOs.DeadLetter
{
    public class ResolveDeadLetterRequestDTO
    {
        public string ResolutionNotes { get; set; }
        public string ResolvedBy { get; set; }
    }
}
