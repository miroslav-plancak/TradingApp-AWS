using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace TradingApp.API.Hubs
{
    public abstract class BaseHub<T> : Hub
    {
        protected readonly ILogger<T> _logger;
        protected BaseHub(ILogger<T> logger)
        {
            _logger = logger;
        }

        public override Task OnConnectedAsync()
        {
            _logger.LogInformation("{HubName} client connected | ConnectionId: {ConnectionId}", typeof(T).Name, Context.ConnectionId);

            return base.OnConnectedAsync();
        }
    }
}
