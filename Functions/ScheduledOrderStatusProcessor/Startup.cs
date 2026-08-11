using Amazon.Lambda.Annotations;
using Handler;
using Microsoft.Extensions.DependencyInjection;
using TradingApp.Infrastructure;

namespace LambdaBootstrap
{
    [LambdaStartup]
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddTradingAppLogging();
            services.AddTradingDbContextFactory();
            services.AddSnsClient();
            services.AddResiliencePolicy(ResiliencePolicyKey.Sql, "ScheduledOrderStatusProcessor-Sql");
            services.AddResiliencePolicy(ResiliencePolicyKey.Messaging, "order_events_topic");
            services.AddIntegrationEventPublisherServices();

            // Overrides the generator's default AddSingleton<ScheduledOrderStatusProcessor>()
            // (registered before this method runs) - without this, the Scoped TradingDbContext it
            // depends on gets captured once at cold start and reused for the life of the execution environment.
            services.AddScoped<ScheduledOrderStatusProcessor>();
        }
    }
}
