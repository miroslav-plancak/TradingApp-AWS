using System;

namespace TradingApp.Business.DTOs.DeadLetter
{
    public class CreateDeadLetterRequestDTO
    {
        public Guid ClientOrderId { get; set; }
        public string MessageBody { get; set; }
        public string Reason { get; set; }
        public string CorrelationId { get; set; }
    }
}
