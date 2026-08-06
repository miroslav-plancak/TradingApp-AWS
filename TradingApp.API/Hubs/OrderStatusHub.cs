using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace TradingApp.API.Hubs
{
    public class OrderStatusHub : Hub
    {
        private readonly ILogger<OrderStatusHub> _logger;
        public OrderStatusHub(ILogger<OrderStatusHub> logger)
        {
            _logger = logger;
        }

        public override Task OnConnectedAsync()
        {
            _logger.LogInformation("SignalR client connected | ConnectionId: {ConnectionId}", Context.ConnectionId);

            return base.OnConnectedAsync();
        }
    }
}
