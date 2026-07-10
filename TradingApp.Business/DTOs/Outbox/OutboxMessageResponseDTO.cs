using System;

namespace TradingApp.Business.DTOs.Outbox
{
    public class OutboxMessageResponseDTO
    {
        public Guid Id { get; set; }
        public string Type { get; set; }
        public string Payload { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ProcessedAt { get; set; }
        public int RetryCount { get; set; }
        public bool IsProcessed { get; set; }
    }
}
