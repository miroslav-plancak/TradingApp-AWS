using Amazon.Lambda.Annotations;
using Handler;
using Handler.Interfaces;
using Handler.Services;
using Handler.Settings;
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
            services.AddSqsClient();
            services.AddResiliencePolicy(ResiliencePolicyKey.Sql, "OutboxProcessingService-Sql");
            services.AddResiliencePolicy(ResiliencePolicyKey.Messaging, "CREATE_ORDER_QUEUE");

            services.AddScoped<IOutboxQuarantineService, OutboxQuarantineService>();
            services.AddScoped<IOutboxProcessingService, OutboxProcessingService>();
            services.AddScoped<IOutboxRecoveryService, OutboxRecoveryService>();

            services.AddSingleton<OutboxMessageProcessorSettings>();

            // Overrides the generator's default AddSingleton<ScheduledOutboxMessageProcessor>()
            // (registered before this method runs) - without this, the Scoped TradingDbContext it
            // depends on gets captured once at cold start and reused for the life of the execution environment.
            services.AddScoped<ScheduledOutboxMessageProcessor>();
        }
    }
}
