using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using TradingApp.API.Hubs;

namespace TradingApp.API
{
    public static class HubEndpointExtensions
    {
        public static void MapAppHubs(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapHub<EventsHub>("hubs/events");
        }
    }
}
