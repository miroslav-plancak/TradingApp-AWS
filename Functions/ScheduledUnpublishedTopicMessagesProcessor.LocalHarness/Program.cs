using Amazon.Lambda.TestUtilities;
using Amazon.SimpleNotificationService;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;
using TradingApp.Domain;
using TradingApp.Infrastructure;

var services = new ServiceCollection();
services.AddTradingAppLogging();
services.AddTradingDbContext();
services.AddTradingDbContextFactory();
services.AddSnsClient();
services.AddResiliencePolicy("order_events_topic");
var serviceProvider = services.BuildServiceProvider();

Console.WriteLine("Simulating EventBridge Scheduler - running ScheduledUnpublishedTopicMessagesProcessor every 60s... (Ctrl+C to stop)");

while (true)
{
    try
    {
        using var scope = serviceProvider.CreateScope();
        var function = new Handler.ScheduledUnpublishedTopicMessagesProcessor(
            scope.ServiceProvider.GetRequiredService<TradingDbContext>(),
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<TradingDbContext>>(),
            scope.ServiceProvider.GetRequiredService<IAmazonSimpleNotificationService>(),
            scope.ServiceProvider.GetRequiredService<IAsyncPolicy>());

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
