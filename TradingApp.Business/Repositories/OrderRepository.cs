using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.Order;

namespace TradingApp.Business.Repositories
{
    public class OrderRepository : IOrderRepository
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly ILogger<OrderRepository> _logger;

        public OrderRepository(ILogger<OrderRepository> logger, TradingDbContext tradingDbContext)
        {
            _logger = logger;
            _tradingDbContext = tradingDbContext;
        }

        public async Task<Order> CreateOrderAsync(Order order)
        {
            try
            {
                order.Id = Guid.NewGuid();
                order.ClientOrderId = Guid.NewGuid();
                order.CreatedAt = DateTimeOffset.UtcNow;
                order.UpdatedAt = DateTimeOffset.UtcNow;

                _tradingDbContext.Orders.Add(order);
                await _tradingDbContext.SaveChangesAsync();

                return order;
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx,
                    "DatabaseError | Failed to create order | CorrelationId: {CorrelationId}",
                    order.CorrelationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "UnexpectedError | Failed to create order | CorrelationId: {CorrelationId}",
                    order.CorrelationId);
                throw;
            }
        }

        public async Task<Order> GetOrderByIdAsync(Guid orderId)
        {
            try
            {
                var result = await _tradingDbContext.Orders
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == orderId);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "DatabaseError | Failed to get order | OrderId: {OrderId}",
                    orderId);
                throw;
            }
        }

        public async Task<IEnumerable<Order>> GetOrdersAsync()
        {
            try
            {
                var result = await _tradingDbContext.Orders.ToListAsync();
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DatabaseError | Failed to retrieve orders");
                throw;
            }
        }

        public async Task<bool> DeleteOrderAsync(Guid orderId)
        {
            try
            {
                var order = await _tradingDbContext.Orders
                    .SingleOrDefaultAsync(x => x.Id == orderId);

                if (order == null)
                {
                    return false;
                }

                _tradingDbContext.Orders.Remove(order);
                await _tradingDbContext.SaveChangesAsync();

                return true;
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx,
                    "DatabaseError | Failed to delete order | OrderId: {OrderId}",
                    orderId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "UnexpectedError | Failed to delete order | OrderId: {OrderId}",
                    orderId);
                throw;
            }
        }

        public async Task<int> DeleteAllOrdersAsync()
        {
            try
            {
                var orders = await _tradingDbContext.Orders.ToListAsync();
                var count = orders.Count;

                if (count > 0)
                {
                    _tradingDbContext.Orders.RemoveRange(orders);
                    await _tradingDbContext.SaveChangesAsync();
                }

                return count;
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx, "DatabaseError | Failed to delete all orders");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UnexpectedError | Failed to delete all orders");
                throw;
            }
        }
    }
}