using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Domain.Models.Entities.Order;

namespace TradingApp.Business.Interfaces.Repositories
{
    public interface IOrderRepository
    {
        Task<Order> CreateOrderAsync(Order order);
        Task<IEnumerable<Order>> GetOrdersAsync();
        Task<Order> GetOrderByIdAsync(Guid orderId);
        Task<bool> DeleteOrderAsync(Guid orderId);
        Task<int> DeleteAllOrdersAsync();
    }
}
