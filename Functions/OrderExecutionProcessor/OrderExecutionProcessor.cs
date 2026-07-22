using Amazon.Lambda.Annotations;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.EntityFrameworkCore;
using Polly.CircuitBreaker;
using System.Text.Json;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.Order;
using TradingApp.Domain.Models.Entities.UnpublishedTopicMessages;
using TradingApp.Domain.Models.Enums;
using TradingApp.Events.Events;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace OrderExecutionProcessor
{
    public class OrderExecutionProcessor
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly string _orderEventsTopicArn;
        private readonly AsyncCircuitBreakerPolicy _circuitBreaker;

        private static int _topicFailureCount = 0;

        public OrderExecutionProcessor(
            TradingDbContext tradingDbContext, 
            IAmazonSimpleNotificationService snsClient,
            AsyncCircuitBreakerPolicy circuitBreakerPolicy
            )
        {
            _tradingDbContext = tradingDbContext;
            _snsClient = snsClient;
            _orderEventsTopicArn = Environment.GetEnvironmentVariable("ORDER_EVENTS_TOPIC_ARN")
                ?? throw new InvalidOperationException("ORDER_EVENTS_TOPIC_ARN environment variable is not set.");
            _circuitBreaker = circuitBreakerPolicy;
        }

        [LambdaFunction]
        public async Task FunctionHandler(SQSEvent evnt, ILambdaContext context)
        {
            foreach (var record in evnt.Records)
            {
                await ProcessOrderMessage(record, context);
            }
        }

        private async Task ProcessOrderMessage(SQSEvent.SQSMessage record, ILambdaContext context)
        {
            SimulateRedirectToDeadLetterQueue(false);

            SQSEvent.MessageAttribute? correlationIdAttribute = null;
            var hasRealCorrelationId = record.MessageAttributes != null
                && record.MessageAttributes.TryGetValue("CorrelationId", out correlationIdAttribute)
                && !string.IsNullOrEmpty(correlationIdAttribute.StringValue);

            var correlationId = hasRealCorrelationId ? correlationIdAttribute!.StringValue : record.MessageId;

            context.Logger.LogWarning(
                $"OrderExecutionStarted with {(hasRealCorrelationId ? "real" : "substitute")} " +
                $"correlationId | CorrelationId: {correlationId} | MessageId: {record.MessageId}");

            var payload = JsonSerializer.Deserialize<OrderPayload>(record.Body);

            if (payload == null)
            {
                context.Logger.LogError(
                    $"InvalidPayload | CorrelationId: {correlationId} | MessageId: {record.MessageId}");
                return;
            }

            // TODO: unbounded DB calls are even more dangerous here than on Azure - Lambda's configured
            // timeout is 15 SECONDS by default (not the up-to-15-minute Consumption plan window this
            // comment used to refer to), so a hung SQL call kills the whole invocation much faster.
            // Add a command timeout once the connection string / DbContext setup is finalized.
            var orderExists = await _tradingDbContext.Orders
                .AnyAsync(o => o.ClientOrderId == payload.ClientOrderId);

            if (!orderExists)
            {
                context.Logger.LogWarning(
                    $"OrderNotFound | CorrelationId: {correlationId} | ClientOrderId: {payload.ClientOrderId}");
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
                context.Logger.LogWarning(
                    $"OrderAlreadyProcessed | CorrelationId: {correlationId} | ClientOrderId: {payload.ClientOrderId}");
                return;
            }

            context.Logger.LogWarning(
                $"OrderProcessed | CorrelationId: {correlationId} | ClientOrderId: {payload.ClientOrderId} | Status: {randomStatus}");

            await PublishOrderProcessedEvent(payload.ClientOrderId, randomStatus, correlationId, context);
        }

        private async Task PublishOrderProcessedEvent(Guid clientOrderId, OrderStatus status, string correlationId, ILambdaContext context)
        {
            try
            {
                var eventPayload = new OrderStatusEvent
                {
                    ClientOrderId = clientOrderId,
                    Status = status.ToString(),
                    EventTime = DateTimeOffset.UtcNow,
                    Sequence = 1,
                    CorrelationId = correlationId
                };

                var messageBody = JsonSerializer.Serialize(eventPayload);

                var request = new PublishRequest
                {
                    TopicArn = _orderEventsTopicArn,
                    Message = messageBody,
                    Subject = "OrderProcessed",
                    MessageGroupId = clientOrderId.ToString(),
                    MessageDeduplicationId = Guid.NewGuid().ToString()
                };

                context.Logger.LogWarning(
                    $"PublishingEventToTopic | CorrelationId: {correlationId} | ClientOrderId: {clientOrderId} | Topic: order_events_topic.fifo");

                await _circuitBreaker.ExecuteAsync(async () =>
                {
                    SimulateTopicFailure(false, context);

                    await _snsClient.PublishAsync(request);

                });

                context.Logger.LogWarning(
                    $"EventPublishedToTopic | CorrelationId: {correlationId} | ClientOrderId: {clientOrderId} | Topic: order_events_topic.fifo");
            }
            catch (BrokenCircuitException)
            {
                context.Logger.LogWarning(
                    $"CircuitOpen | CorrelationId: {correlationId} | Order is {status}, event not yet published | Falling back to UnpublishedTopicMessages");

                _tradingDbContext.UnpublishedTopicMessages.Add(new UnpublishedTopicMessage
                {
                    Id = Guid.NewGuid(),
                    ClientOrderId = clientOrderId,
                    OrderStatus = status,
                    ProcessedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow,
                    CorrelationId = correlationId
                });

                await _tradingDbContext.SaveChangesAsync();

                context.Logger.LogWarning(
                    $"SavedToUnpublishedTopicMessages | CorrelationId: {correlationId} | ClientOrderId: {clientOrderId}");
            }
            catch (AmazonSimpleNotificationServiceException snsException)
            {
                context.Logger.LogError(
                    $"TopicPublishFailed | CorrelationId: {correlationId} | ClientOrderId: {clientOrderId} | Error: {snsException.Message}");

                _tradingDbContext.UnpublishedTopicMessages.Add(new UnpublishedTopicMessage
                {
                    Id = Guid.NewGuid(),
                    ClientOrderId = clientOrderId,
                    OrderStatus = status,
                    ProcessedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow,
                    CorrelationId = correlationId
                });

                await _tradingDbContext.SaveChangesAsync();

                context.Logger.LogWarning(
                    $"SavedToUnpublishedTopicMessages | CorrelationId: {correlationId} | ClientOrderId: {clientOrderId}");
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"EventPublishFailed | CorrelationId: {correlationId} | ClientOrderId: {clientOrderId} | Error: {ex.Message}");
            }

        }

        private void SimulateTopicFailure(bool isTopicDown, ILambdaContext context)
        {
            if (!isTopicDown) return;

            _topicFailureCount++;

            if (_topicFailureCount <= 3)
            {
                context.Logger.LogWarning(
                    $"SIMULATION | Simulating topic outage | FailureCount: {_topicFailureCount}");

                throw new InternalErrorException(
                    $"SIMULATED: Topic connection failed (failure {_topicFailureCount} of 3)");
            }
        }

        private void SimulateRedirectToDeadLetterQueue(bool active) 
        {
            if (!active) return;

            throw new Exception("SIMULATED: Message Redirected to DLQ.");
        }
    }
}