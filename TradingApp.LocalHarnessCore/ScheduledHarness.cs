using Amazon.Lambda.Core;
using Amazon.Lambda.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace TradingApp.LocalHarnessCore
{
    public static class ScheduledHarness
    {
        public static async Task RunAsync(
            IServiceProvider serviceProvider,
            Func<IServiceProvider, ILambdaContext, Task> handler,
            string? lambdaName = null
            )
        {
            Console.WriteLine(lambdaName ?? $"Simulating EventBridge Scheduler - {lambdaName}  every 60s... (Ctrl+C to stop)");
            while (true)
            {
                try
                {
                    using var scope = serviceProvider.CreateScope();
                    await handler(scope.ServiceProvider, new TestLambdaContext());
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Tick failed: {ex.Message}");
                    // We do not throw exception here so a failure during one tick (for example - a database connection drop)
                    // does not crash the loop and kill the local background process.
                }

                await Task.Delay(TimeSpan.FromMinutes(1));
            }
        }
    }
}
