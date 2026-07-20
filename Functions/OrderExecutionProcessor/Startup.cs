using Amazon.Lambda.Annotations;
using Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace LambdaBootstrap
{
    [LambdaStartup]
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddTradingAppLogging();
            services.AddTradingDbContext();
            services.AddSnsClient();
        }
    }
}
