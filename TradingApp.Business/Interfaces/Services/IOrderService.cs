using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Order;

namespace TradingApp.Business.Interfaces.Services
{
    public interface IOrderService
    {
        Task<CreatedOrderResponseDTO> CreateOrderAsync(CreateOrderRequestDTO createOrder);
        Task <IEnumerable<OrderResponseDTO>> GetOrdersAsync();
        Task <OrderResponseDTO> GetOrderByIdAsync(Guid orderId);
        Task<bool> DeleteOrderAsync(Guid orderId);
        Task<int> DeleteAllOrdersAsync();
    }
}
