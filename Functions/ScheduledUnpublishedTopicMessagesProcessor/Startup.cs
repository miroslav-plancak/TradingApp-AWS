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
            services.AddTradingDbContext();
            services.AddTradingDbContextFactory();
            services.AddSnsClient();
            services.AddCircuitBreakerPolicy("order_events_topic");

            // Overrides the generator's default AddSingleton<ScheduledUnpublishedTopicMessagesProcessor>()
            // (registered before this method runs) - without this, the Scoped TradingDbContext it
            // depends on gets captured once at cold start and reused for the life of the execution environment.
            services.AddScoped<ScheduledUnpublishedTopicMessagesProcessor>();
        }
    }
}
