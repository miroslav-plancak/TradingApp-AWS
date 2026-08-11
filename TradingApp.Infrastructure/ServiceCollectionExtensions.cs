using Amazon;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Microsoft.Data.SqlClient;
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
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Services;

namespace TradingApp.Infrastructure
{
    public static class ServiceCollectionExtensions
    {
        private const int SqlCommandTimeoutSeconds = 10;

        private static readonly HashSet<int> _sqlServerTransientErrorNumbers = new()
        {
            4060,   // Cannot open database requested by the login
			10928,  // Resource limit reached
			10929,
            40197,  // The service has encountered an error processing your request
			40501,  // The service is currently busy
			40613,  // Database unavailable
			49918,  // Cannot process request. Not enough resources
			49919,
            49920,
            1205,   // Deadlock
			233,    // Connection initialization error
			64,     // Network-related error
			-2      // Timeout
		};

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
            services.AddDbContext<TradingDbContext>(options =>
                options.UseSqlServer(GetSqlConnectionString(), 
                options => options.CommandTimeout(SqlCommandTimeoutSeconds)));

            return services;
        }

        public static IServiceCollection AddTradingDbContextFactory(this IServiceCollection services)
        {
            services.AddDbContextFactory<TradingDbContext>(options =>
                options.UseSqlServer(GetSqlConnectionString(), 
                options => options.CommandTimeout(SqlCommandTimeoutSeconds)));

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
        public static IServiceCollection AddIntegrationEventPublisherServices(this IServiceCollection services)
        {
            services.AddScoped<IIntegrationEventPublisher, IntegrationEventPublisher>();
            return services;
        }

        public static IServiceCollection AddDeadLetterServices(this IServiceCollection services)
        {
            services.AddScoped<IDeadLetterRepository, DeadLetterRepository>();
            services.AddScoped<IDeadLetterService, DeadLetterService>();
            services.AddSingleton<HttpClient>();
            return services;
        }

        // For classes that talk to exactly one downstream dependency through _resiliencePolicy - a
        // shared circuit breaker is fine there, since there's nothing else it could wrongly block.
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

        // For classes that do BOTH SQL writes and an SNS/SQS call - registers a separate, independently
        // circuited IAsyncPolicy per policyKey, so a downed SQL Server can't trip the same breaker that
        // guards the topic/queue publish (or vice versa). Resolve via [FromKeyedServices(policyKey)].
        public static IServiceCollection AddResiliencePolicy(this IServiceCollection services, ResiliencePolicyKey policyKey, string protectedResourceName)
        {
            services.AddKeyedSingleton<IAsyncPolicy>(policyKey, (sp, _) =>
            {
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("ResiliencePolicy");

                var retryPolicy = BuildRetryPolicy(logger, protectedResourceName, policyKey);
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

        private static AsyncRetryPolicy BuildRetryPolicy(ILogger logger, string protectedResourceName, ResiliencePolicyKey? policyKey = null)
        {
            return Policy
                .Handle<Exception>(exception => IsTransientException(exception, policyKey))
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: CalculateSleepDuration,
                    onRetry: (exception, delay, attempt, ctx) =>
                        logger.LogWarning(
                            "RetryAttempt {Attempt} | {Resource} | Waiting {Delay}ms | Error: {Error}",
                            attempt, protectedResourceName, delay.TotalMilliseconds, exception.Message));
        }

        private static bool IsTransientException(Exception exception, ResiliencePolicyKey? policyKey)
        {
            return policyKey == ResiliencePolicyKey.Messaging
                ? IsTransientAWSException(exception)
                : IsTransientSQLException(exception);
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
            
           return AreThereAnyTransientHttpExceptions(exception); 
        }

        private static bool IsTransientSQLException(Exception exception)
        {
            if (exception is SqlException sqlEx)
            {
                return _sqlServerTransientErrorNumbers.Contains(sqlEx.Number);
            }
          
           return AreThereAnyTransientHttpExceptions(exception);
         
        }

        private static bool AreThereAnyTransientHttpExceptions(Exception exception)
        {
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