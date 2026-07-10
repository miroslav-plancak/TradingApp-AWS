using System;

namespace TradingApp.Business.DTOs.Order
{
    public class CreatedOrderResponseDTO
    {
        public Guid Id { get; set; }
        public Guid ClientOrderId { get; set; }
        public string Status { get; set; }
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public bool IsProcessed { get; set; }
        public string CorrelationId { get; set; }
    }
}
