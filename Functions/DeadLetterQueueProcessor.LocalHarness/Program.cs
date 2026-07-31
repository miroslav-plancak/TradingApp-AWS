using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Domain;
using TradingApp.Infrastructure;

var services = new ServiceCollection();
services.AddTradingAppLogging();
services.AddTradingDbContext();
services.AddDeadLetterServices();
services.AddResiliencePolicy("DeadLetterQueueProcessor.LocalHarness");
var serviceProvider = services.BuildServiceProvider();

var sqsClient = new AmazonSQSClient(Amazon.RegionEndpoint.EUNorth1);
var dlqUrl = "https://sqs.eu-north-1.amazonaws.com/465861110788/CREATE_ORDER_QUEUE-DLQ.fifo";

Console.WriteLine("Listening for dead-lettered messages on CREATE_ORDER_QUEUE-DLQ.fifo... (Ctrl+C to stop)");

while (true)
{
    var response = await sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
    {
        QueueUrl = dlqUrl,
        MaxNumberOfMessages = 10,
        WaitTimeSeconds = 20,
        MessageAttributeNames = new List<string> { "All" },
        MessageSystemAttributeNames = new List<string> { "ApproximateReceiveCount" }
    });

    foreach (var message in response.Messages ?? new List<Message>())
    {
        try
        {
            using var scope = serviceProvider.CreateScope();

            var function = new DeadLetterQueueProcessor.DeadLetterQueueProcessor(
                scope.ServiceProvider.GetRequiredService<TradingDbContext>(),
                scope.ServiceProvider.GetRequiredService<IDeadLetterService>(),
                scope.ServiceProvider.GetRequiredService<HttpClient>(),
                scope.ServiceProvider.GetRequiredService<IAsyncPolicy>()
                );

            var sqsEvent = new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>
                  {
                      new SQSEvent.SQSMessage
                      {
                          MessageId = message.MessageId,
                          Body = message.Body,
                          ReceiptHandle = message.ReceiptHandle,
                          Attributes = message.Attributes,
                          MessageAttributes = message.MessageAttributes?.ToDictionary(
                              kvp => kvp.Key, kvp => new SQSEvent.MessageAttribute
                              {
                                  DataType = kvp.Value.DataType,
                                  StringValue = kvp.Value.StringValue
                              })
                      }
                  }
            };

            await function.FunctionHandler(sqsEvent, new TestLambdaContext());
            await sqsClient.DeleteMessageAsync(dlqUrl, message.ReceiptHandle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Message {message.MessageId} failed: {ex.Message}");
            // deliberately not deleted - stays on the DLQ for inspection/retry
        }
    }
}