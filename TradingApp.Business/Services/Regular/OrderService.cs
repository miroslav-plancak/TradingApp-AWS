using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Order;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Business.Mappers;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.OutboxMessage;

namespace TradingApp.Business.Services.Regular
{
    public class OrderService : IOrderService
    {
        private readonly IOrderRepository _orderRepository;
        private readonly TradingDbContext _tradingDbContext;
        private readonly ILogger<OrderService> _logger;

        public OrderService(
            ILogger<OrderService> logger,
            TradingDbContext tradingDbContext,
            IOrderRepository orderRepository)
        {
            _logger = logger;
            _tradingDbContext = tradingDbContext;
            _orderRepository = orderRepository;
        }

        public async Task<CreatedOrderResponseDTO> CreateOrderAsync(CreateOrderRequestDTO orderRequest)
        {
            var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString();

            _logger.LogInformation(
                "OrderCreationStarted | CorrelationId: {CorrelationId}",
                correlationId);

            using var transaction = await _tradingDbContext.Database.BeginTransactionAsync();

            try
            {
                var orderEntityRequest = OrderMapper.ToEntity(orderRequest);
                orderEntityRequest.CorrelationId = correlationId; 

                var order = await _orderRepository.CreateOrderAsync(orderEntityRequest);

                _tradingDbContext.OutboxMessages.Add(new OutboxMessage
                {
                    Id = Guid.NewGuid(),
                    Type = "OrderCreated",
                    Payload = order.ClientOrderId.ToString(),
                    CreatedAt = DateTimeOffset.UtcNow,
                    CorrelationId = correlationId 
                });

                await _tradingDbContext.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation(
                    "OrderCreated | CorrelationId: {CorrelationId} | OrderId: {OrderId} | ClientOrderId: {ClientOrderId}",
                    correlationId, order.Id, order.ClientOrderId);

                var createdOrderResponseDTO = OrderMapper.ToCreatedOrderResponseDTO(order);

                createdOrderResponseDTO.CorrelationId = correlationId;

                return createdOrderResponseDTO;
            }
            catch (DbUpdateException dbEx)
            {
                await transaction.RollbackAsync();

                _logger.LogError(dbEx,
                    "DatabaseError | CorrelationId: {CorrelationId} | Error: {Message}",
                    correlationId, dbEx.Message);

                throw new Exception("Failed to create order due to database error", dbEx);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();

                _logger.LogError(ex,
                    "OrderCreationFailed | CorrelationId: {CorrelationId} | Error: {Message}",
                    correlationId, ex.Message);

                throw new Exception("Failed to create order", ex);
            }
        }

        public async Task<OrderResponseDTO> GetOrderByIdAsync(Guid orderId)
        {
            _logger.LogInformation("GetOrderById | OrderId: {OrderId}", orderId);

            try
            {
                var orderEntity = await _orderRepository.GetOrderByIdAsync(orderId);

                if (orderEntity == null)
                {
                    _logger.LogWarning("OrderNotFound | OrderId: {OrderId}", orderId);
                    throw new KeyNotFoundException($"Order {orderId} not found.");
                }

                var orderDTO = OrderMapper.ToOrderResponseDTO(orderEntity);

                _logger.LogInformation("OrderRetrieved | OrderId: {OrderId} | CorrelationId: {CorrelationId}",
                    orderId, orderEntity.CorrelationId);

                return orderDTO;
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetOrderByIdFailed | OrderId: {OrderId}", orderId);
                throw new Exception($"Failed to retrieve order {orderId}", ex);
            }
        }

        public async Task<IEnumerable<OrderResponseDTO>> GetOrdersAsync()
        {
            _logger.LogInformation("GetOrders");

            try
            {
                var orderEntities = await _orderRepository.GetOrdersAsync();
                var orderDTOs = OrderMapper.ToOrderResponseDTOs(orderEntities);

                _logger.LogInformation("OrdersRetrieved | Count: {Count}", orderDTOs.Count());

                return orderDTOs;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetOrdersFailed");
                throw new Exception("Failed to retrieve orders", ex);
            }
        }

        public async Task<bool> DeleteOrderAsync(Guid orderId)
        {
            _logger.LogInformation("DeleteOrder | OrderId: {OrderId}", orderId);

            try
            {
                var orderEntity = await _orderRepository.GetOrderByIdAsync(orderId);

                if (orderEntity == null)
                {
                    _logger.LogWarning("OrderNotFoundForDeletion | OrderId: {OrderId}", orderId);
                    throw new KeyNotFoundException($"Order {orderId} not found.");
                }

                var deleted = await _orderRepository.DeleteOrderAsync(orderId);

                _logger.LogInformation("OrderDeleted | OrderId: {OrderId} | CorrelationId: {CorrelationId}",
                    orderId, orderEntity.CorrelationId);

                return deleted;
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeleteOrderFailed | OrderId: {OrderId}", orderId);
                throw new Exception($"Failed to delete order {orderId}", ex);
            }
        }

        public async Task<int> DeleteAllOrdersAsync()
        {
            _logger.LogInformation("DeleteAllOrders");

            try
            {
                var deletedCount = await _orderRepository.DeleteAllOrdersAsync();

                _logger.LogInformation("AllOrdersDeleted | Count: {Count}", deletedCount);

                return deletedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeleteAllOrdersFailed");
                throw new Exception("Failed to delete all orders", ex);
            }
        }
    }
}