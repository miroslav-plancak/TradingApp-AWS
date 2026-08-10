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
        private readonly IHubContext<OrderStatusHub> _hubContext;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SignalRPushBackgroundService> _logger;
        private readonly IAmazonSQS _sqsClient;
        private readonly string _queueUrl;

        public SignalRPushBackgroundService
        (
            IHubContext<OrderStatusHub> hubContext,
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
                        WaitTimeSeconds = 20
                    }, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch(Exception ex)
                {
                    _logger.LogError(ex, "ReceiveMessage failed on {QueueUrl}, backing off 5s", _queueUrl);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                foreach(var message in response.Messages ?? new List<Message>())
                {
                    try
                    {
                        var orderEvent = JsonSerializer.Deserialize<IntegrationEvent>(message.Body);

                        using var scope = _scopeFactory.CreateScope();
                        var orderService = scope.ServiceProvider.GetRequiredService<IOrderService>();
                        var order = await orderService.GetOrderByClientOrderIdAsync(orderEvent.ClientOrderId);

                        // The full order, not just id+status - clients apply it directly with
                        // no follow-up fetch (upsert into the entity store, same shape RequestCurrentStatus returns).
                        await _hubContext.Clients.All.SendAsync("OrderStatusChanged", order, stoppingToken);

                        await _sqsClient.DeleteMessageAsync(_queueUrl, message.ReceiptHandle, stoppingToken);
                    }
                    catch (KeyNotFoundException ex)
                    {
                        // The order genuinely doesn't exist - retrying won't fix that, so drop the message.
                        _logger.LogWarning(ex, "OrderNotFoundForPush | MessageId: {MessageId} - discarding, not retrying", message.MessageId);
                        await _sqsClient.DeleteMessageAsync(_queueUrl, message.ReceiptHandle, stoppingToken);
                    }
                    catch(Exception ex)
                    {
                        _logger.LogError(ex, "Failed processing message {MessageId} - left on queue for retry", message.MessageId);
                    }
                }
            }
        }
    }
}
