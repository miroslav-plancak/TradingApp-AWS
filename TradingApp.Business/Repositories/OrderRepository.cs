using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.Order;

namespace TradingApp.Business.Repositories
{
    public class OrderRepository : IOrderRepository
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IResiliencePolicyGuard _resiliencePolicyGuard;

        public OrderRepository
        (
            TradingDbContext tradingDbContext,
            IResiliencePolicyGuard resiliencePolicyGuard
        )
        {
            _tradingDbContext = tradingDbContext;
            _resiliencePolicyGuard = resiliencePolicyGuard;
        }

        public async Task<Order> CreateOrderAsync(Order order)
        {
            await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
            {
                order.Id = Guid.NewGuid();
                order.ClientOrderId = Guid.NewGuid();
                order.CreatedAt = DateTimeOffset.UtcNow;
                order.UpdatedAt = DateTimeOffset.UtcNow;

                _tradingDbContext.Orders.Add(order);
                await _tradingDbContext.SaveChangesAsync();
            },
            $"{nameof(CreateOrderAsync)}:Save:{order.CorrelationId}");

            return order;
        }

        public async Task<Order> GetOrderByIdAsync(Guid orderId)
        {
            return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.Orders
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == orderId),
                $"{nameof(GetOrderByIdAsync)}:Fetch:{orderId}");
        }

        public async Task<Order> GetOrderByClientOrderIdAsync(Guid clientOrderId)
        {
            return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.Orders
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ClientOrderId == clientOrderId),
                $"{nameof(GetOrderByClientOrderIdAsync)}:Fetch:{clientOrderId}");
        }

        public async Task<IEnumerable<Order>> GetOrdersAsync()
        {
            return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.Orders.AsNoTracking().ToListAsync(),
                nameof(GetOrdersAsync) + ":Fetch");
        }

        public async Task<bool> DeleteOrderAsync(Guid orderId)
        {
            var order = await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.Orders
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == orderId),
                $"{nameof(DeleteOrderAsync)}:Fetch:{orderId}");

            if (order == null)
            {
                return false;
            }

            await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
            {
                _tradingDbContext.Orders.Remove(order);
                await _tradingDbContext.SaveChangesAsync();
            },
            $"{nameof(DeleteOrderAsync)}:Delete:{orderId}");

            return true;
        }

        public async Task<int> DeleteAllOrdersAsync()
        {
            var orders = await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.Orders.AsNoTracking().ToListAsync(),
                nameof(DeleteAllOrdersAsync) + ":Fetch");

            var count = orders.Count;

            if (count > 0)
            {
                await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                {
                    _tradingDbContext.Orders.RemoveRange(orders);
                    await _tradingDbContext.SaveChangesAsync();
                },
                nameof(DeleteAllOrdersAsync) + ":Delete");
            }

            return count;
        }
    }
}
