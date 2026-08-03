using LambdaBootstrap;
using Microsoft.Extensions.DependencyInjection;
using TradingApp.LocalHarnessCore;

var services = new ServiceCollection();
new Startup().ConfigureServices(services);
var serviceProvider = services.BuildServiceProvider();

var queueUrl = "https://sqs.eu-north-1.amazonaws.com/465861110788/audit_log_queue.fifo";

await SqsHarness.RunAsync(queueUrl, serviceProvider, async (sp, sqsEvent, context) => 
{
    var function = sp.GetRequiredService<AuditLogProcessor.AuditLogProcessor>();
    await function.FunctionHandler(sqsEvent, context);
}, "Listening for order status events on audit_log_queue.fifo... (Ctrl+C to stop)");

