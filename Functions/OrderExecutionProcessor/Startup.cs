using Amazon.Lambda.Annotations;
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
            services.AddResiliencePolicy(ResiliencePolicyKey.Sql, "OrderExecutionProcessor-Sql");
            services.AddResiliencePolicy(ResiliencePolicyKey.Aws, "order_events_topic");
            services.AddSnsClient();
            services.AddIntegrationEventPublisherServices();

            // Overrides the generator's default AddSingleton<OrderExecutionProcessor>() (registered
            // before this method runs) - without this, the Scoped TradingDbContext it depends on gets
            // captured and held "hostage" once at cold start and reused for the lifetime of the execution
            // environment.
            // global:: is required here - the namespace and class share the same name, so a plain
            // `using` resolves the bare identifier to the NAMESPACE, not the class (CS0118).
            services.AddScoped<global::OrderExecutionProcessor.OrderExecutionProcessor>();
        }
    }
}
