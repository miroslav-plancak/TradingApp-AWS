using System;
using TradingApp.Domain.Models.Enums;

namespace TradingApp.Domain.Models.Entities.Order
{
    public class OrderResponse
    {
        public Guid ClientOrderId {get;set;}
        public OrderStatus Status { get; set; }
    }
}
