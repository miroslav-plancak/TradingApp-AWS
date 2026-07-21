using Amazon.Lambda.TestUtilities;
using Amazon.SQS;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;
using TradingApp.Domain;
using TradingApp.Infrastructure;

var services = new ServiceCollection();
services.AddTradingAppLogging();
services.AddTradingDbContext();
services.AddSqsClient();
services.AddCircuitBreakerPolicy("CREATE_ORDER_QUEUE");
var serviceProvider = services.BuildServiceProvider();

Console.WriteLine("Simulating EventBridge Scheduler - running ScheduledOutboxMessageProcessor every 60s... (Ctrl+C to stop)");

while (true)
{
    try
    {
        using var scope = serviceProvider.CreateScope();
        var function = new Handler.ScheduledOutboxMessageProcessor(
            scope.ServiceProvider.GetRequiredService<TradingDbContext>(),
            scope.ServiceProvider.GetRequiredService<IAmazonSQS>(),
            scope.ServiceProvider.GetRequiredService<AsyncCircuitBreakerPolicy>());

        await function.FunctionHandler(new TestLambdaContext());
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Tick failed: {ex.Message}");
        // deliberately not rethrown - one bad tick shouldn't kill the harness,
        // same reasoning as the ReceiveMessageAsync guard we discussed earlier
    }

    await Task.Delay(TimeSpan.FromMinutes(1));
}
