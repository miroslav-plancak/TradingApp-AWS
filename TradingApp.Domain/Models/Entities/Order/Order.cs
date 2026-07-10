using System;
using TradingApp.Domain.Models.Enums;

namespace TradingApp.Domain.Models.Entities.Order
{
    public class Order
    {
        public Guid Id { get; set; }
        public Guid ClientOrderId { get; set; }
        public OrderStatus Status { get; set; }
        public int Quantity { get; set; }   
        public decimal Price { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public bool IsProcessed { get; set; }
        public string CorrelationId { get; set; } = string.Empty;
    }
}
