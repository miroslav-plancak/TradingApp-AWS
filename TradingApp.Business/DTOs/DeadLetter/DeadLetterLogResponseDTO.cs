using System;

namespace TradingApp.Business.DTOs.DeadLetter
{
    public class DeadLetterLogResponseDTO
    {
        public Guid Id { get; set; }
        public Guid ClientOrderId { get; set; }
        public string Reason { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public bool IsResolved { get; set; }
        public string ResolutionNotes { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }
        public string ResolvedBy { get; set; }
        public string MessageBody { get; set; }
        public string CorrelationId { get; set; }
    }
}
