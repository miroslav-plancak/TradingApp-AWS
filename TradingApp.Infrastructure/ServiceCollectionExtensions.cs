using Amazon;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Anthropic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using StackExchange.Redis;
using System.Net.Http.Headers;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Business.Repositories;
using TradingApp.Business.Services.Regular;
using TradingApp.Domain;
using TradingApp.Infrastructure.Helpers;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Services;

namespace TradingApp.Infrastructure
{
    public static class ServiceCollectionExtensions
    {
        private const int SqlCommandTimeoutSeconds = 10;

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

        public static IServiceCollection AddRedisConnection(this IServiceCollection services)
        {
            services.AddSingleton<IConnectionMultiplexer>((sp) =>
            {
                var configuration = ConfigurationOptions.Parse("localhost:6379");
                configuration.Protocol = RedisProtocol.Resp2;
                return ConnectionMultiplexer.Connect(configuration);
            });

            return services;
        }

        public static IServiceCollection AddIntegrationEventPublisherServices(this IServiceCollection services)
        {
            services.AddScoped<IIntegrationEventPublisher, IntegrationEventPublisher>();
            return services;
        }

        public static IServiceCollection AddVoyageEmbeddingServices(this IServiceCollection services)
        {
            services.AddHttpClient<IVoyageEmbeddingService, VoyageEmbeddingService>((sp, client) =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                var apiKey = configuration["Voyage:ApiKey"]
                    ?? throw new InvalidOperationException("Voyage:ApiKey configuration value is not set.");

                client.BaseAddress = new Uri("https://api.voyageai.com/v1/");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            });

            return services;
        }

        public static IServiceCollection AddVoyageRerankingServices(this IServiceCollection services)
        {
            services.AddHttpClient<IVoyageRerankService, VoyageRerankService>((sp, client) =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                var apiKey = configuration["Voyage:ApiKey"]
                    ?? throw new InvalidOperationException("Voyage:ApiKey configuration value is not set.");

                client.BaseAddress = new Uri("https://api.voyageai.com/v1/");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            });

            return services;
        }

        public static IServiceCollection AddChunkingIngestionService(this IServiceCollection services)
        {
            services.AddScoped<IChunkIngestionService, ChunkIngestionService>();
            return services;
        }

        public static IServiceCollection AddChunkingRetrievalService(this IServiceCollection services)
        {
            services.AddScoped<IChunkRetrievalService, ChunkRetrievalService>();
            return services;
        }

        public static IServiceCollection AddChunkRerankingService(this IServiceCollection services)
        {
            services.AddScoped<IChunkRerankingService, ChunkRerankingService>();
            return services;
        }

        public static IServiceCollection AddKnowledgeBaseQueryService(this IServiceCollection services)
        {
            services.AddScoped<IKnowledgeBaseQueryService, KnowledgeBaseQueryService>();
            return services;
        }

        public static IServiceCollection AddQueryRoutingServices(this IServiceCollection services)
        {
            services.AddScoped<IQueryRoutingService, QueryRoutingService>();
            return services;
        }

        public static IServiceCollection AddFileExpansionService(this IServiceCollection services)
        {
            services.AddScoped<IFileExpansionService, FileExpansionService>();
            return services;
        }

        public static IServiceCollection AddAnthropicClient(this IServiceCollection services)
        {
            services.AddSingleton(sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                var apiKey = configuration["Anthropic:ApiKey"]
                    ?? throw new InvalidOperationException("Anthropic:ApiKey configuration value is not set.");

                return new AnthropicClient { ApiKey = apiKey, MaxRetries = 0 };
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

        // For classes that talk to exactly one downstream dependency through _resiliencePolicy - a
        // shared circuit breaker is fine there, since there's nothing else it could wrongly block.
        public static IServiceCollection AddResiliencePolicy(this IServiceCollection services, string protectedResourceName)
        {
            services.AddSingleton<IAsyncPolicy>(sp =>
            {
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("ResiliencePolicy");

                var retryPolicy = ResiliencePolicyBuilder.BuildRetryPolicy(logger, protectedResourceName);
                var circuitBreakerPolicy = ResiliencePolicyBuilder.BuildCircuitBreakerPolicy(logger, protectedResourceName);

                var resiliencePolicy = circuitBreakerPolicy.WrapAsync(retryPolicy);

                return resiliencePolicy;
            });

            return services;
        }

        // For classes that do BOTH SQL writes and an SNS/SQS call - registers a separate, independently
        // circuited IAsyncPolicy per policyKey, so a downed SQL Server can't trip the same breaker that
        // guards the topic/queue publish (or vice versa). This is resolved via [FromKeyedServices(policyKey)].
        public static IServiceCollection AddResiliencePolicy
        (
            this IServiceCollection services,
            ResiliencePolicyKey policyKey,
            string protectedResourceName,
            int retryCount = 3,
            Func<int, TimeSpan>? sleepDurationProvider = null
        )
        {
            services.AddKeyedSingleton<IAsyncPolicy>(policyKey, (sp, _) =>
            {
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("ResiliencePolicy");

                var retryPolicy = ResiliencePolicyBuilder.BuildRetryPolicy(logger, protectedResourceName, policyKey, retryCount, sleepDurationProvider);
                var circuitBreakerPolicy = ResiliencePolicyBuilder.BuildCircuitBreakerPolicy(logger, protectedResourceName);

                var resiliencePolicy = circuitBreakerPolicy.WrapAsync(retryPolicy);

                return resiliencePolicy;
            });

            return services;
        }
    }
}