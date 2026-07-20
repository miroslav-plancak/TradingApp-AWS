using Amazon.Lambda.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Infrastructure;

namespace LambdaBootstrap
{
    [LambdaStartup]
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddTradingAppLogging();
            services.AddTradingDbContext();
            services.AddDeadLetterServices();
        }
    }
}
