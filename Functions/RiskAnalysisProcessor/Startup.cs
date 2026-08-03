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

            services.AddScoped<RiskAnalysisProcessor.RiskAnalysisProcessor>();
        }
    }
}
