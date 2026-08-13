using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingApp.API.BackgroundServices;
using TradingApp.Events.Events;

namespace TradingApp.API.PushDispatch
{
    public static class PushEventDispatchLoop
    {

        public async static Task RunAsync
        (
            ILogger<SignalRPushBackgroundService> logger,
            string queueUrl,
            PushEventCallback handler,
            CancellationToken stoppingToken
        )
        {
            logger.LogInformation("SignalR push listener started on {QueueUrl}", queueUrl);
            var _sqsClient = new AmazonSQSClient(RegionEndpoint.EUNorth1);

            while (!stoppingToken.IsCancellationRequested)
            {
                ReceiveMessageResponse response;

                try
                {
                    response = await _sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MaxNumberOfMessages = 10,
                        WaitTimeSeconds = 20,
                        MessageAttributeNames = new List<string> { "All" }
                    }, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "ReceiveMessage failed on {QueueUrl}, backing off 5s", queueUrl);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                foreach (var message in response.Messages ?? new List<Message>())
                {
                    try
                    {
                        var integrationEvent = JsonSerializer.Deserialize<IntegrationEvent>(message.Body);

                        var eventType = message.MessageAttributes["EventType"].StringValue;
                        var pushEventOutcome = await handler(eventType, integrationEvent, stoppingToken);

                        bool shouldDeleteMessage;

                        switch (pushEventOutcome)
                        {
                            case PushEventOutcome.SUCCESS:

                                logger.LogWarning("EventPushedSuccesfully | MessageId: {MessageId} - discarding the message, not retrying.", message.MessageId);
                                shouldDeleteMessage = true;
                                break;

                            case PushEventOutcome.FAILURE:

                                logger.LogWarning("EntityNotFoundForPush | MessageId: {MessageId} - discarding the message, not retrying.", message.MessageId);
                                shouldDeleteMessage = true;
                                break;

                            case PushEventOutcome.INVALIDEVENTREGISTRYKEY:

                                logger.LogWarning("InvalidRegistryKeyProvided | MessageId: {MessageId} - discarding the message, not retrying.", message.MessageId);
                                shouldDeleteMessage = true;
                                break;

                            default:

                                logger.LogError("UnhandledPushEventOutcome | Outcome: {Outcome} | MessageId: {MessageId} - no explicit case for this outcome, leaving on queue for redelivery.", pushEventOutcome, message.MessageId);
                                shouldDeleteMessage = false;
                                break;
                        }

                        if (shouldDeleteMessage)
                        {
                            await _sqsClient.DeleteMessageAsync(queueUrl, message.ReceiptHandle, stoppingToken);
                        }
                    }
                    // In case we run into unforeesen exception, we retry.
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed processing message {MessageId} - left on queue for retry", message.MessageId);
                    }
                }
            }

        }

    }
}
