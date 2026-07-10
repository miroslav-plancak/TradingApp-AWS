using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.UnpublishedTopicMessages;
using TradingApp.Domain.Models.Enums;
using TradingApp.Events.Events;
using TradingApp.Events.Payloads;

namespace OrderExecutionProvider
{
    public class OrderExecutionProvider
    {
        private readonly ILogger<OrderExecutionProvider> _logger;
        private readonly TradingDbContext _tradingDbContext;
        private readonly ServiceBusClient _serviceBusClient;
        private readonly ServiceBusSender _sender;

        public OrderExecutionProvider
        (
            ILogger<OrderExecutionProvider> logger,
            TradingDbContext tradingDbContext,
            ServiceBusClient serviceBusClient
        )
        {
            _logger = logger;
            _tradingDbContext = tradingDbContext;
            _serviceBusClient = serviceBusClient;
            _sender = _serviceBusClient.CreateSender("order_events_topic");
        }

        [Function("OrderExecutionProvider")]
        public async Task Run
        (
            [ServiceBusTrigger(
                queueName: "CREATE_ORDER_QUEUE",
                Connection = "ServiceBusConnection")]
            ServiceBusReceivedMessage message
        )
        {
            RedirectIncomingMessagesToDeadLetterQueue(false);

            var correlationId = message.CorrelationId ?? "CorrelationId";

            _logger.LogWarning(
                "OrderExecutionStarted | CorrelationId: {CorrelationId} | MessageId: {MessageId}",
                correlationId, message.MessageId);

            var payload = JsonSerializer.Deserialize<OrderPayload>(message.Body.ToString());

            if (payload == null)
            {
                _logger.LogError(
                    "InvalidPayload | CorrelationId: {CorrelationId} | MessageId: {MessageId}",
                    correlationId, message.MessageId);
                return;
            }

            // TODO: this and all other SQL connections can hang indefinitely when not configured
            // which would make the OrderExecutionProvider.cs func run for up to 15 min -> at which
            // point Azure reclaims the resource and the function is shut down. This is a problem here
            // and anywhere else where I am making a database call via EF Core, so fix it everywhere
            // by adding .CommandTimeout(30) to the DbContext registration in Program.cs.
            var orderExists = await _tradingDbContext.Orders
                .AnyAsync(o => o.ClientOrderId == payload.ClientOrderId);

            if (!orderExists)
            {
                _logger.LogWarning(
                    "OrderNotFound | CorrelationId: {CorrelationId} | ClientOrderId: {ClientOrderId}",
                    correlationId, payload.ClientOrderId);
                return;
            }

            var random = new Random();
            var randomStatus = random.Next(2) == 0 ? OrderStatus.ACKNOWLEDGED : OrderStatus.REJECTED;

            var orderRowsProcessed = await _tradingDbContext.Orders
                .Where(x => x.ClientOrderId == payload.ClientOrderId && !x.IsProcessed)
                .ExecuteUpdateAsync(x => x
                    .SetProperty(x => x.Status, randomStatus)
                    .SetProperty(x => x.IsProcessed, true)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow));

            if (orderRowsProcessed == 0)
            {
                _logger.LogWarning(
                    "OrderAlreadyProcessed | CorrelationId: {CorrelationId} | ClientOrderId: {ClientOrderId}",
                    correlationId, payload.ClientOrderId);
                return;
            }

            _logger.LogWarning(
                "OrderProcessed | CorrelationId: {CorrelationId} | ClientOrderId: {ClientOrderId} | Status: {Status}",
                correlationId, payload.ClientOrderId, randomStatus);

            await PublishOrderProcessedEvent(payload.ClientOrderId, randomStatus, correlationId);
        }

        private async Task PublishOrderProcessedEvent(Guid clientOrderId, OrderStatus randomStatus, string correlationId)
        {
            try
            {
                var eventPayload = new OrderStatusEvent
                {
                    ClientOrderId = clientOrderId,
                    Status = randomStatus.ToString(),
                    EventTime = DateTimeOffset.UtcNow,
                    Sequence = 1,
                    CorrelationId = correlationId
                };

                var messageBody = JsonSerializer.Serialize(eventPayload);

                var message = new ServiceBusMessage(messageBody)
                {
                    MessageId = Guid.NewGuid().ToString(),
                    ContentType = "application/json",
                    Subject = "OrderProcessed",
                    SessionId = clientOrderId.ToString()
                };

                _logger.LogWarning(
                    "PublishingEventToTopic | CorrelationId: {CorrelationId} | ClientOrderId: {ClientOrderId} | Topic: order_events_topic",
                    correlationId, clientOrderId);

                SimulateTopicFailure(false);
                await _sender.SendMessageAsync(message);

                _logger.LogWarning(
                    "EventPublishedToTopic | CorrelationId: {CorrelationId} | ClientOrderId: {ClientOrderId} | Topic: order_events_topic",
                    correlationId, clientOrderId);
            }
            catch (ServiceBusException serviceBusException)
            {
                _logger.LogError(serviceBusException,
                    "TopicPublishFailed | CorrelationId: {CorrelationId} | ClientOrderId: {ClientOrderId} | Error: {Message}",
                    correlationId, clientOrderId, serviceBusException.Message);

                _tradingDbContext.UnpublishedTopicMessages.Add(new UnpublishedTopicMessage
                {
                    Id = Guid.NewGuid(),
                    ClientOrderId = clientOrderId,
                    OrderStatus = randomStatus,
                    ProcessedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow,
                    CorrelationId = correlationId 
                });

                await _tradingDbContext.SaveChangesAsync();

                _logger.LogWarning(
                    "SavedToUnpublishedTopicMessages | CorrelationId: {CorrelationId} | ClientOrderId: {ClientOrderId}",
                    correlationId, clientOrderId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "EventPublishFailed | CorrelationId: {CorrelationId} | ClientOrderId: {ClientOrderId}",
                    correlationId, clientOrderId);
            }
        }

        private void SimulateTopicFailure(bool isServiceBusDown) 
        {
            if (!isServiceBusDown) return;

            throw new ServiceBusException(
                    "SIMULATED: Topic down",
                    ServiceBusFailureReason.ServiceCommunicationProblem);
        }

        private void RedirectIncomingMessagesToDeadLetterQueue(bool active) 
        {
            if (!active) return;

            throw new ServiceBusException(
                "SIMULATED: Incoming messages redirected to DeadLetterQueue.", 
                ServiceBusFailureReason.GeneralError);
        }
    }
}