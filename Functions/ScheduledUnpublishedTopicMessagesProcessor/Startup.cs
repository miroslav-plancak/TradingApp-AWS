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
            services.AddResiliencePolicy(ResiliencePolicyKey.Sql, "ScheduledUnpublishedTopicMessagesProcessor-Sql");
            services.AddResiliencePolicy(ResiliencePolicyKey.Aws, "order_events_topic");

            // Overrides the generator's default AddSingleton<ScheduledUnpublishedTopicMessagesProcessor>()
            // (registered before this method runs) - without this, the Scoped TradingDbContext it
            // depends on gets captured and held "hostage" once at cold start and reused for the lifetime
            // of the execution environment.
            services.AddScoped<ScheduledUnpublishedTopicMessagesProcessor>();
        }
    }
}
