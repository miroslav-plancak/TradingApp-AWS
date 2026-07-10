using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;

namespace ScheduledOrderStatusProcessor.Extensions
{
    public static class ServiceCollectioExtensions
    {
        public static IServiceCollection AddServiceBusCircuitBreaker(this IServiceCollection services) 
        {
            services.AddSingleton<AsyncCircuitBreakerPolicy>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<ScheduledOrderStatusProcessor>>();
                return Policy
                       .Handle<Exception>()
                       .CircuitBreakerAsync(
                            exceptionsAllowedBeforeBreaking:3,
                            durationOfBreak: TimeSpan.FromMinutes(2),

                            onBreak: (exception, duration) =>
                                logger.LogWarning(
                                    "CircuitBreaker OPENED | Topic unreachable | Will retry in {Duration}s | Error: {Error}",
                                    duration.TotalSeconds, exception.Message),

                            onReset: () =>
                                logger.LogWarning(
                                      "CircuitBreaker CLOSED | Topic connectivity restored"),
                             onHalfOpen: () =>
                                logger.LogWarning(
                                      "CircuitBreaker HALF-OPEN | Testing topic connectivity...")
                    );
            });

            return services;
        }
    }
}
