using LambdaBootstrap;
using Microsoft.Extensions.DependencyInjection;
using TradingApp.LocalHarnessCore;

var services = new ServiceCollection();
new Startup().ConfigureServices(services);
var serviceProvider = services.BuildServiceProvider();

var queueUrl = "https://sqs.eu-north-1.amazonaws.com/465861110788/CREATE_ORDER_QUEUE.fifo";

await SqsHarness.RunAsync(queueUrl, serviceProvider, async (sp, sqsEvent, context) =>
{
    var function = sp.GetRequiredService<OrderExecutionProcessor.OrderExecutionProcessor>();
    return await function.FunctionHandler(sqsEvent, context);
},
"Listening for real messages on CREATE_ORDER_QUEUE.fifo... (Ctrl+C to stop)",
// This applies only to the failed messages, which we artificially divert to DeadLetterQueue via
// SimulateRedirectToDeadLetterQueue(true). Every other message still respects the native queue
// 120sec VisibilityTimeout.
failureVisibilityTimeoutSeconds: 10);
