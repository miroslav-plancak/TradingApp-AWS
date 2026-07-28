using Amazon;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using System.Net;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Business.Repositories;
using TradingApp.Business.Services.Regular;
using TradingApp.Domain;

namespace TradingApp.Infrastructure
{
    public static class ServiceCollectionExtensions
    {
        private static string GetSqlConnectionString()
        {
            return Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING")
                        ?? throw new InvalidOperationException("SQL_CONNECTION_STRING environment variable not set.");
        }

        public static IServiceCollection AddTradingAppLogging(this IServiceCollection services)
        {
            services.AddLogging(builder => builder.AddConsole());
            return services;
        }

        public static IServiceCollection AddTradingDbContext(this IServiceCollection services)
        {
            services.AddDbContext<TradingDbContext>(options => options.UseSqlServer(GetSqlConnectionString()));
            return services;
        }

        public static IServiceCollection AddTradingDbContextFactory(this IServiceCollection services)
        {
            services.AddDbContextFactory<TradingDbContext>(options => options.UseSqlServer(GetSqlConnectionString()));
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

        public static IServiceCollection AddDeadLetterServices(this IServiceCollection services)
        {
            services.AddScoped<IDeadLetterRepository, DeadLetterRepository>();
            services.AddScoped<IDeadLetterService, DeadLetterService>();
            services.AddSingleton<HttpClient>();
            return services;
        }

        public static IServiceCollection AddResiliencePolicy(this IServiceCollection services, string protectedResourceName)
        {
            services.AddSingleton<IAsyncPolicy>(sp => 
            {
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("ResiliencePolicy");

                var retryPolicy = BuildRetryPolicy(logger, protectedResourceName);
                var circuitBreakerPolicy = BuildCircuitBreakerPolicy(logger, protectedResourceName);

                var resiliencePolicy = circuitBreakerPolicy.WrapAsync(retryPolicy);

                return resiliencePolicy;
            });

            return services;
        }

        private static AsyncCircuitBreakerPolicy BuildCircuitBreakerPolicy(ILogger logger, string protectedResourceName)
        {
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
        }

        private static AsyncRetryPolicy BuildRetryPolicy(ILogger logger, string protectedResourceName)
        {
            return Policy
                .Handle<Exception>(IsTransientAWSException)
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: CalculateSleepDuration,
                    onRetry: (exception, delay, attempt, ctx) =>
                        logger.LogWarning(
                            "RetryAttempt {Attempt} | {Resource} | Waiting {Delay}ms | Error: {Error}",
                            attempt, protectedResourceName, delay.TotalMilliseconds, exception.Message));
        }

        private static bool IsTransientAWSException(Exception exception)
        {
            if(exception is AmazonServiceException awsEx)
            {
                if (awsEx.ErrorType == ErrorType.Receiver) 
                    return true;
               
                return awsEx.StatusCode == HttpStatusCode.TooManyRequests ||     // 429 - throttling
                       awsEx.StatusCode == HttpStatusCode.ServiceUnavailable ||  // 503
                       awsEx.StatusCode == HttpStatusCode.BadGateway ||          // 502   
                       awsEx.StatusCode == HttpStatusCode.GatewayTimeout ||      // 504
                       awsEx.StatusCode == HttpStatusCode.RequestTimeout;        // 408  
            }

            if (exception is HttpRequestException || exception is TimeoutException) 
                return true;

            if (ContainsTransientKeyword(exception.Message)) 
                return true;
           
            if (exception.GetType().Name.Contains("Transient", StringComparison.OrdinalIgnoreCase)) 
                return true;

            return false;
        }

        private static bool ContainsTransientKeyword(string message)
        {
            if (string.IsNullOrEmpty(message)) 
                return false;

            string[] transientKeywords = { "transient", "retryable", "temporarily unavailable", "timeout", "throttl" };

            return transientKeywords.Any(keyWord => message.Contains(keyWord, StringComparison.OrdinalIgnoreCase));
        }

        private static TimeSpan CalculateSleepDuration(int attempt)
        {
            var delaySeconds = Math.Pow(2, attempt);
            return TimeSpan.FromSeconds(delaySeconds);
        }
    }
}