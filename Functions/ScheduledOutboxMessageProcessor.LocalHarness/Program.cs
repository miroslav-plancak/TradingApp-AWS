using Handler;
using LambdaBootstrap;
using Microsoft.Extensions.DependencyInjection;
using TradingApp.LocalHarnessCore;

var services = new ServiceCollection();
new Startup().ConfigureServices(services);
var serviceProvider = services.BuildServiceProvider();

await ScheduledHarness.RunAsync(serviceProvider, async (sp, context) =>
{
    var function = sp.GetRequiredService<ScheduledOutboxMessageProcessor>();
    await function.FunctionHandler(context);
}, "ScheduledOutboxMessageProcessor every 60s... (Ctrl+C to stop)");
