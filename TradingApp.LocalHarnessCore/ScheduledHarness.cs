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
                    // deliberately not rethrown - one bad tick shouldn't kill the harness,
                    // same reasoning as the ReceiveMessageAsync guard we discussed earlier
                }

                await Task.Delay(TimeSpan.FromMinutes(1));
            }
        }
    }
}
