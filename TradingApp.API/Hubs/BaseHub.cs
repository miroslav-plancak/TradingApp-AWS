using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace TradingApp.API.Hubs
{
    public abstract class BaseHub : Hub
    {
        protected readonly ILogger _logger;
        protected BaseHub(ILogger logger)
        {
            _logger = logger;
        }

        public override Task OnConnectedAsync()
        {
            _logger.LogInformation("{HubName} client connected | ConnectionId: {ConnectionId}", GetType().Name, Context.ConnectionId);

            return base.OnConnectedAsync();
        }
    }
}
