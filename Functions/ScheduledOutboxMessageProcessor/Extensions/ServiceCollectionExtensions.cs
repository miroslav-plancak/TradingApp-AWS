using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;

namespace ScheduledOutboxMessageProcessor.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddServiceBusCircuitBreaker(this IServiceCollection services)
        {
            services.AddSingleton<AsyncCircuitBreakerPolicy>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<ScheduledOutboxMessageProcessor>>();

                return Policy
                      .Handle<Exception>()
                      .CircuitBreakerAsync(
                          exceptionsAllowedBeforeBreaking: 3,
                          durationOfBreak: TimeSpan.FromMinutes(2),
                          onBreak: (exception, duration) =>
                              logger.LogWarning(
                                  "CircuitBreaker OPENED | ServiceBus unreachable | Will retry in {Duration}s | Error: {Error}",
                                  duration.TotalSeconds, exception.Message),
                          onReset: () =>
                              logger.LogWarning(
                                  "CircuitBreaker CLOSED | ServiceBus connectivity restored"),
                          onHalfOpen: () =>
                              logger.LogWarning(
                                  "CircuitBreaker HALF-OPEN | Testing ServiceBus connectivity..."));
            });

            return services;
        }
    }
}
