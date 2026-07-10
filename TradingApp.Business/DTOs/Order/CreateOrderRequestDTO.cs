using TradingApp.Domain.Models.Enums;

namespace TradingApp.Business.DTOs.Order
{
    public class CreateOrderRequestDTO
    {
        public int Quantity { get; set; }
        public decimal Price { get; set; }
    }
}
