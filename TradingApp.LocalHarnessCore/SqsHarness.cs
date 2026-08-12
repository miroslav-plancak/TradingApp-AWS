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
            Func<IServiceProvider, SQSEvent, ILambdaContext, Task<SQSBatchResponse>> handler,
            string? listeningMessage = null,
            // Only takes effect on a message the handler actually reports as failed - never touches
            // the queue's real VisibilityTimeout, so normal (non-failing) messages are unaffected.
            // Used exclusively by OrderExecutionProcessor.LocalHarness for the purposes of deliberately
            // shortening the wait time between the redeliveries of messages to the queue by reducing
            // their VisibilityTimeout programmatically from 120s to 10s.
            int? failureVisibilityTimeoutSeconds = null
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

                        var batchResponse = await handler(scope.ServiceProvider, sqsEvent, new TestLambdaContext());

                        // In a deployed Lambda the AWS would read this batchResponse.BatchItemFailures and have these
                        // messages left for an SQS redelivery (bumping up the maxReceiveCount towards redrive policy).
                        // In our localharness we have to do this manually to mimic the behavior - we skip deleting every
                        // message that handler has reported as failed.
                        var reportedFailure = batchResponse?.BatchItemFailures?
                            .Any(f => f.ItemIdentifier == message.MessageId) ?? false;

                        if (reportedFailure)
                        {
                            var receiveCount = message.Attributes != null
                                && message.Attributes.TryGetValue("ApproximateReceiveCount", out var countStr)
                                ? countStr
                                : "unknown";

                            Console.WriteLine(
                                $"Message {message.MessageId} reported as failed (ApproximateReceiveCount={receiveCount}) " +
                                $"- left on queue for SQS to redeliver/redrive.");

                            if (failureVisibilityTimeoutSeconds is int seconds)
                            {
                                await sqsClient.ChangeMessageVisibilityAsync(queueUrl, message.ReceiptHandle, seconds);

                                Console.WriteLine(
                                    $"Message {message.MessageId} visibility timeout shortened to {seconds}s for faster redrive testing.");
                            }
                        }
                        else
                        {
                            await sqsClient.DeleteMessageAsync(queueUrl, message.ReceiptHandle);
                        }
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
