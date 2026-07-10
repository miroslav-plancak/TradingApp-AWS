using Microsoft.Extensions.DependencyInjection;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Business.Middleware;
using TradingApp.Business.Repositories;
using TradingApp.Business.Services.Regular;

namespace TradingApp.Business
{
    public static class BusinessServiceCollectionExtensions
    {
        public static IServiceCollection RegisterBusiness(this IServiceCollection services)
        {
            services.AddTransient<ExceptionHandlingMiddleware>();
            services.AddScoped<IOrderService, OrderService>()
                    .AddScoped<IOrderRepository, OrderRepository>()
                    .AddScoped<IDeadLetterService, DeadLetterService>()
                    .AddScoped<IDeadLetterRepository, DeadLetterRepository>()
                    .AddScoped<IOutboxMessageService, OutboxMessageService>()
                    .AddScoped<IOutboxMessageRepository, OutboxMessageRepository>();

            return services;
        }
    }
}
