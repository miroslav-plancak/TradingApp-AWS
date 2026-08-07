using LambdaBootstrap;
using Microsoft.Extensions.DependencyInjection;
using TradingApp.LocalHarnessCore;

var services = new ServiceCollection();
new Startup().ConfigureServices(services);
var serviceProvider = services.BuildServiceProvider();

var queueUrl = "https://sqs.eu-north-1.amazonaws.com/465861110788/notification_queue.fifo";

await SqsHarness.RunAsync(queueUrl, serviceProvider, async (sp, sqsEvent, context) =>
{
    var function = sp.GetRequiredService<NotificationProcessor.NotificationProcessor>();
    return await function.FunctionHandler(sqsEvent, context);
}, "Listening for order status events on notification_queue.fifo... (Ctrl+C to stop)");
