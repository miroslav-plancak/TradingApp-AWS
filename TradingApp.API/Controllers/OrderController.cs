using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Order;
using TradingApp.Business.Interfaces.Services;

namespace TradingApp.API.Controllers
{
    public class OrderController : TradingAppBaseController<OrderController>
    {
        private readonly IOrderService _orderService;

        public OrderController(
            ILogger<OrderController> logger,
            IOrderService orderService)
            : base(logger)
        {
            _orderService = orderService;
        }

        [HttpPost]
        [ProducesResponseType(typeof(CreatedOrderResponseDTO), 200)]
        public async Task<ActionResult> CreateOrderAsync(CreateOrderRequestDTO createOrder)
        {
            _logger.LogInformation("CreateOrderRequest received");

            var result = await _orderService.CreateOrderAsync(createOrder);
            return Ok(result);
        }

        [HttpGet("{orderId}")]
        [ProducesResponseType(typeof(OrderResponseDTO), 200)]
        [ProducesResponseType(404)]
        public async Task<ActionResult> GetOrderByIdAsync([FromRoute] Guid orderId)
        {
            _logger.LogInformation("GetOrderByIdRequest | OrderId: {OrderId}", orderId);

            var result = await _orderService.GetOrderByIdAsync(orderId);
            return Ok(result);
        }

        [HttpGet]
        [ProducesResponseType(typeof(IEnumerable<OrderResponseDTO>), 200)]
        public async Task<ActionResult> GetOrdersAsync()
        {
            _logger.LogInformation("GetOrdersRequest");

            var result = await _orderService.GetOrdersAsync();
            return Ok(result);
        }

        [HttpDelete("{orderId}")]
        [ProducesResponseType(typeof(bool), 200)]
        [ProducesResponseType(404)]
        public async Task<ActionResult> DeleteOrderAsync([FromRoute] Guid orderId)
        {
            _logger.LogInformation("DeleteOrderRequest | OrderId: {OrderId}", orderId);

            var result = await _orderService.DeleteOrderAsync(orderId);
            return Ok(result);
        }

        [HttpDelete]
        [ProducesResponseType(200)]
        public async Task<ActionResult> DeleteAllOrdersAsync()
        {
            _logger.LogInformation("DeleteAllOrdersRequest");

            var deletedCount = await _orderService.DeleteAllOrdersAsync();
            return Ok(new { deletedCount });
        }
    }
}