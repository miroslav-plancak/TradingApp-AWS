using Amazon;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Business.Repositories;
using TradingApp.Business.Services.Regular;
using TradingApp.Domain;

namespace Infrastructure
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddTradingAppLogging(this IServiceCollection services)
        {
            services.AddLogging(builder => builder.AddConsole());
            return services;
        }

        public static IServiceCollection AddTradingDbContext(this IServiceCollection services)
        {
            var connectionString = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING")
                ?? throw new InvalidOperationException("SQL_CONNECTION_STRING environment variable is not set.");

            services.AddDbContext<TradingDbContext>(options => options.UseSqlServer(connectionString));
            return services;
        }

        public static IServiceCollection AddSqsClient(this IServiceCollection services)
        {
            services.AddSingleton<IAmazonSQS>(new AmazonSQSClient(RegionEndpoint.EUNorth1));
            return services;
        }

        public static IServiceCollection AddSnsClient(this IServiceCollection services)
        {
            services.AddSingleton<IAmazonSimpleNotificationService>(
                new AmazonSimpleNotificationServiceClient(RegionEndpoint.EUNorth1));
            return services;
        }

        public static IServiceCollection AddSharedHttpClient(this IServiceCollection services)
        {
            services.AddSingleton<HttpClient>();
            return services;
        }

        public static IServiceCollection AddCircuitBreakerPolicy(this IServiceCollection services, string protectedResourceName)
        {
            services.AddSingleton(sp =>
            {
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CircuitBreaker");

                return Policy
                    .Handle<Exception>()
                    .CircuitBreakerAsync(
                        exceptionsAllowedBeforeBreaking: 3,
                        durationOfBreak: TimeSpan.FromMinutes(2),
                        onBreak: (exception, duration) =>
                            logger.LogWarning(
                                "CircuitBreaker OPENED | {Resource} unreachable | Will retry in {Duration}s | Error: {Error}",
                                protectedResourceName, duration.TotalSeconds, exception.Message),
                        onReset: () =>
                            logger.LogWarning("CircuitBreaker CLOSED | {Resource} connectivity restored", protectedResourceName),
                        onHalfOpen: () =>
                            logger.LogWarning("CircuitBreaker HALF-OPEN | Testing {Resource} connectivity...", protectedResourceName));
            });

            return services;
        }

        public static IServiceCollection AddDeadLetterServices(this IServiceCollection services)
        {
            services.AddScoped<IDeadLetterRepository, DeadLetterRepository>();
            services.AddScoped<IDeadLetterService, DeadLetterService>();
            services.AddSingleton<HttpClient>();
            return services;
        }

    }
}