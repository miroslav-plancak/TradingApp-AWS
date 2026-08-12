using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingApp.API.Hubs;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Events.Events;

namespace TradingApp.API.BackgroundServices
{
    public class SignalRPushBackgroundService : BackgroundService
    {
        private readonly IHubContext<EventsHub> _hubContext;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SignalRPushBackgroundService> _logger;
        private readonly IAmazonSQS _sqsClient;
        private readonly string _queueUrl;

        public SignalRPushBackgroundService
        (
            IHubContext<EventsHub> hubContext,
            IServiceScopeFactory scopeFactory,
            ILogger<SignalRPushBackgroundService> logger
        )
        {
            _hubContext = hubContext;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _sqsClient = new AmazonSQSClient(RegionEndpoint.EUNorth1);
            _queueUrl = Environment.GetEnvironmentVariable("SIGNALR_PUSH_QUEUE_URL")
                ?? throw new InvalidOperationException("SIGNALR_PUSH_QUEUE_URL environment variable is not set.");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("SignalR push listener started on {QueueUrl}", _queueUrl);

            while (!stoppingToken.IsCancellationRequested)
            {
                ReceiveMessageResponse response;

                try
                {
                    response = await _sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                    {
                        QueueUrl = _queueUrl,
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
                    _logger.LogError(ex, "ReceiveMessage failed on {QueueUrl}, backing off 5s", _queueUrl);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                foreach (var message in response.Messages ?? new List<Message>())
                {
                    try
                    {
                        var orderEvent = JsonSerializer.Deserialize<IntegrationEvent>(message.Body);
                        using var scope = _scopeFactory.CreateScope();

                        var eventType = message.MessageAttributes["EventType"].StringValue;

                        switch (eventType)
                        {
                            case nameof(OrderStatusChangedEvent):

                                var orderService = scope.ServiceProvider.GetRequiredService<IOrderService>();
                                var order = await orderService.GetOrderByClientOrderIdAsync(orderEvent.ClientOrderId);
                                await _hubContext.Clients.All.SendAsync(nameof(OrderStatusChangedEvent), order, stoppingToken);
                                break;

                            case nameof(DeadLetterLogPersistedEvent):

                                var deadLetterLogService = scope.ServiceProvider.GetRequiredService<IDeadLetterService>();
                                var deadLetterLog = await deadLetterLogService.GetByClientOrderIdAsync(orderEvent.ClientOrderId);
                                await _hubContext.Clients.All.SendAsync(nameof(DeadLetterLogPersistedEvent), deadLetterLog, stoppingToken);
                                break;

                            case nameof(OutboxMessageProcessedEvent):

                                var outboxMessageService = scope.ServiceProvider.GetRequiredService<IOutboxMessageService>();
                                var outboxMessage = await outboxMessageService.GetByClientOrderIdAsync(orderEvent.ClientOrderId);
                                await _hubContext.Clients.All.SendAsync(nameof(OutboxMessageProcessedEvent), outboxMessage, stoppingToken);
                                break;

                            default:
                                _logger.LogWarning(
                                    "UnrecognizedEventType | EventType: {EventType} | MessageId: {MessageId} - discarding, no handler registered",
                                    eventType, message.MessageId);
                                break;
                        }

                        await _sqsClient.DeleteMessageAsync(_queueUrl, message.ReceiptHandle, stoppingToken);
                    }
                    catch (KeyNotFoundException ex)
                    {
                        // At this point we are sure that the entity we are trying to find genuinely doesn't exist, which means that
                        // retrying won't fix this issue, so we delete the message we are trying to push to the client from the queue.
                        _logger.LogWarning(ex, "OrderNotFoundForPush | MessageId: {MessageId} - discarding, not retrying", message.MessageId);
                        await _sqsClient.DeleteMessageAsync(_queueUrl, message.ReceiptHandle, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed processing message {MessageId} - left on queue for retry", message.MessageId);
                    }
                }
            }
        }
    }
}
