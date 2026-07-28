using Amazon.Lambda.TestUtilities;
using Amazon.SQS;
using Handler.Interfaces;
using Handler.Services;
using Handler.Settings;
using Microsoft.Extensions.DependencyInjection;
using TradingApp.Infrastructure;

var services = new ServiceCollection();
services.AddTradingAppLogging();
services.AddTradingDbContext();
services.AddTradingDbContextFactory();
services.AddSqsClient();
services.AddResiliencePolicy("CREATE_ORDER_QUEUE");

services.AddScoped<IOutboxQuarantineService, OutboxQuarantineService>();
services.AddScoped<IOutboxProcessingService, OutboxProcessingService>();
services.AddScoped<IOutboxRecoveryService, OutboxRecoveryService>();

services.AddSingleton<OutboxMessageProcessorSettings>();

var serviceProvider = services.BuildServiceProvider();

Console.WriteLine("Simulating EventBridge Scheduler - running ScheduledOutboxMessageProcessor every 60s... (Ctrl+C to stop)");

while (true)
{
    try
    {
        using var scope = serviceProvider.CreateScope();
        var function = new Handler.ScheduledOutboxMessageProcessor(
            scope.ServiceProvider.GetRequiredService<IAmazonSQS>(),
            scope.ServiceProvider.GetRequiredService<IOutboxQuarantineService>(),
            scope.ServiceProvider.GetRequiredService<IOutboxProcessingService>(),
            scope.ServiceProvider.GetRequiredService<IOutboxRecoveryService>(),
            scope.ServiceProvider.GetRequiredService<OutboxMessageProcessorSettings>()
            );

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
