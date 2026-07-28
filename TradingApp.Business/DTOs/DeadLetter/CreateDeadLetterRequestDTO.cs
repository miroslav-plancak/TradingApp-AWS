using System;
using TradingApp.Domain.Models.Enums;

namespace TradingApp.Business.DTOs.DeadLetter
{
    public class CreateDeadLetterRequestDTO
    {
        public Guid ClientOrderId { get; set; }
        public string MessageBody { get; set; }
        public string Reason { get; set; }
        public DeadLetterCategory Category { get; set; }
        public string CorrelationId { get; set; }
    }
}
