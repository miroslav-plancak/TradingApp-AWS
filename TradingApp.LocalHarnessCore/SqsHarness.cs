using Amazon;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;

namespace TradingApp.LocalHarnessCore
{
    public static class SqsHarness
    {
        public static async Task RunAsync(
            string queueUrl,
            IServiceProvider serviceProvider,
            Func<IServiceProvider, SQSEvent, ILambdaContext, Task> handler,
            string? listeningMessage = null
        )
        {
            var sqsClient = new AmazonSQSClient(RegionEndpoint.EUNorth1);

            Console.WriteLine(listeningMessage ?? $"Listening for messages on {queueUrl}... (Ctrl+C to stop)");

            while (true)
            {
                ReceiveMessageResponse response;

                try
                {
                    response = await sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MaxNumberOfMessages = 10,
                        WaitTimeSeconds = 20,
                        MessageAttributeNames = new List<string> { "All" },
                        MessageSystemAttributeNames = new List<string> { "All" }
                    });
                }
                catch (QueueDoesNotExistException ex)
                {
                    Console.WriteLine($"QueueDoesNotExist | {queueUrl} | {ex.Message} | This queue isn't coming back - stopping harness.");
                    return;
                }
                catch (AmazonClientException ex)
                {
                    Console.WriteLine($"ReceiveMessageFailed | {ex.Message} | Backing off 5s before retrying.");
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    continue;
                }

                foreach (var message in response.Messages ?? new List<Message>())
                {
                    try
                    {
                        using var scope = serviceProvider.CreateScope();

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

                        await handler(scope.ServiceProvider, sqsEvent, new TestLambdaContext());
                        await sqsClient.DeleteMessageAsync(queueUrl, message.ReceiptHandle);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Message {message.MessageId} failed: {ex.Message}");
                        // deliberately not deleted - stays on the queue for inspection/retry
                    }
                }
            }
        }
    }
}
