using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.Interfaces.Services;

namespace TradingApp.API.Hubs
{
    public class EventsHub : Hub
    {
        private readonly ILogger<EventsHub> _logger;
        private readonly IOrderService _orderService;
        public EventsHub(ILogger<EventsHub> logger, IOrderService orderService)
        {
            _logger = logger;
            _orderService = orderService;
        }

        public override Task OnConnectedAsync()
        {
            _logger.LogInformation("SignalR client connected | ConnectionId: {ConnectionId}", Context.ConnectionId);

            return base.OnConnectedAsync();
        }

        public async Task RequestCurrentStatus(Guid orderId)
        {
            try
            {
                var order = await _orderService.GetOrderByIdAsync(orderId);
                await Clients.Caller.SendAsync("CurrentOrderStatus", order);
            }
            catch (KeyNotFoundException ex)
            {
                // We deliberately throw HubException here because it is the only exception type SignalR sends
                // the real message for via socket connection - anything else is stripped to a generic error by
                // default "An unexpected error occurred invoking 'MethodName' on the server."
                throw new HubException(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RequestCurrentStatusFailed | OrderId: {OrderId}", orderId);
                throw new HubException("Failed to retrieve current order status.");
            }
        }
    }
}
