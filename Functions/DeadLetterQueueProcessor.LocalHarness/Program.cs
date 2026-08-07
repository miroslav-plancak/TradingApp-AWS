using LambdaBootstrap;
using Microsoft.Extensions.DependencyInjection;
using TradingApp.LocalHarnessCore;

var services = new ServiceCollection();
new Startup().ConfigureServices(services);
var serviceProvider = services.BuildServiceProvider();

var queueUrl = "https://sqs.eu-north-1.amazonaws.com/465861110788/CREATE_ORDER_QUEUE-DLQ.fifo";

await SqsHarness.RunAsync(queueUrl, serviceProvider, async (sp, sqsEvent, context) =>
{
    var function = sp.GetRequiredService<DeadLetterQueueProcessor.DeadLetterQueueProcessor>();
    return await function.FunctionHandler(sqsEvent, context);
},"Listening for dead-lettered messages on CREATE_ORDER_QUEUE-DLQ.fifo... (Ctrl+C to stop)");



