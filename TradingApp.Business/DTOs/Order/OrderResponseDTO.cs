using System;
using TradingApp.Domain.Models.Enums;

namespace TradingApp.Business.DTOs.Order
{
    public class OrderResponseDTO
    {
        public Guid Id { get; set; }
        public Guid ClientOrderId { get; set; }
        public string Status { get; set; }
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public bool IsProcessed { get; set; }
    }
}
