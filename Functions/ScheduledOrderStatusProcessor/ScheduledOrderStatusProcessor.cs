using Amazon.Lambda.Annotations;
using Amazon.Lambda.Core;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.EntityFrameworkCore;
using Polly.CircuitBreaker;
using System.Text.Json;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.UnpublishedTopicMessages;
using TradingApp.Domain.Models.Enums;
using TradingApp.Events.Events;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Handler
{
    public class ScheduledOrderStatusProcessor
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly string _orderEventsTopicArn;
        private readonly AsyncCircuitBreakerPolicy _circuitBreaker;
        private static int _topicFailureCount = 0;

        public ScheduledOrderStatusProcessor(
            TradingDbContext tradingDbContext,
            IAmazonSimpleNotificationService snsClient,
            AsyncCircuitBreakerPolicy circuitBreaker)
        {
            _tradingDbContext = tradingDbContext;
            _snsClient = snsClient;
            _circuitBreaker = circuitBreaker;

            _orderEventsTopicArn = Environment.GetEnvironmentVariable("ORDER_EVENTS_TOPIC_ARN")
                ?? throw new InvalidOperationException("ORDER_EVENTS_TOPIC_ARN environment variable is not set.");
        }

        [LambdaFunction]
        public async Task FunctionHandler(ILambdaContext context)
        {
            context.Logger.LogWarning($"ScheduledOrderStatusProcessor triggered at: {DateTimeOffset.UtcNow}");

            var orders = await _tradingDbContext.Orders
                .Where(ao => ao.Status == OrderStatus.ACKNOWLEDGED)
                .OrderBy(x => x.CreatedAt)
                .Take(50)
                .ToListAsync();

            if (orders.Count == 0)
            {
                context.Logger.LogWarning("NoAcknowledgedOrders | No orders to promote to FILLED");
                return;
            }

            context.Logger.LogWarning($"PromotingOrders | Found {orders.Count} ACKNOWLEDGED orders to promote");

            var successCount = 0;
            var failureCount = 0;
            var circuitOpened = false;

            foreach (var order in orders)
            {
                try
                {
                    context.Logger.LogWarning(
                        $"PromotingOrder | CorrelationId: {order.CorrelationId} | OrderId: {order.Id} " +
                        $"| ClientOrderId: {order.ClientOrderId} | ACKNOWLEDGED => FILLED");

                    var eventPayload = new OrderStatusEvent
                    {
                        ClientOrderId = order.ClientOrderId,
                        Status = OrderStatus.FILLED.ToString(),
                        EventTime = DateTimeOffset.UtcNow,
                        Sequence = 2,
                        CorrelationId = order.CorrelationId
                    };

                    var messageBody = JsonSerializer.Serialize(eventPayload);

                    var request = new PublishRequest
                    {
                        TopicArn = _orderEventsTopicArn,
                        Message = messageBody,
                        Subject = OrderStatus.FILLED.ToString(),
                        MessageGroupId = order.ClientOrderId.ToString(),
                        MessageDeduplicationId = Guid.NewGuid().ToString()
                    };

                    await _circuitBreaker.ExecuteAsync(async () =>
                    {
                        SimulateTopicFailure(true, context);
                        await _snsClient.PublishAsync(request);
                    });

                    order.Status = OrderStatus.FILLED;
                    order.UpdatedAt = DateTimeOffset.UtcNow;
                    successCount++;

                    context.Logger.LogWarning(
                        $"OrderFilledAndPublished | CorrelationId: {order.CorrelationId} | ClientOrderId: {order.ClientOrderId}");
                }
                catch (BrokenCircuitException)
                {
                    circuitOpened = true;
                    context.Logger.LogWarning(
                        $"CircuitOpen | Stopping batch | CorrelationId: {order.CorrelationId} | Order stays ACKNOWLEDGED");
                    break;
                }
                catch (AmazonSimpleNotificationServiceException snsEx)
                {
                    failureCount++;
                    context.Logger.LogError(
                        $"TopicPublishFailed | CorrelationId: {order.CorrelationId} | Order stays ACKNOWLEDGED | Error: {snsEx.Message}");

                    _tradingDbContext.UnpublishedTopicMessages.Add(new UnpublishedTopicMessage
                    {
                        Id = Guid.NewGuid(),
                        ClientOrderId = order.ClientOrderId,
                        OrderStatus = OrderStatus.FILLED,
                        ProcessedAt = DateTimeOffset.UtcNow,
                        CreatedAt = DateTimeOffset.UtcNow,
                        CorrelationId = order.CorrelationId
                    });

                    order.Status = OrderStatus.FILLED;
                    order.UpdatedAt = DateTimeOffset.UtcNow;

                    context.Logger.LogWarning(
                        $"SavedToUnpublishedTopicMessages | CorrelationId: {order.CorrelationId} " +
                        $"| ClientOrderId: {order.ClientOrderId} | Status: {order.Status}");
                }
                catch (Exception ex)
                {
                    failureCount++;
                    context.Logger.LogError(
                        $"UnexpectedError | CorrelationId: {order.CorrelationId} | Order stays ACKNOWLEDGED | Error: {ex.Message}");
                }
            }

            await _tradingDbContext.SaveChangesAsync();

            GenerateLogBasedOnResults(successCount, failureCount, circuitOpened, orders.Count, context);
        }

        private static void GenerateLogBasedOnResults(int successCount, int failureCount, bool circuitOpened, int totalOrderCount, ILambdaContext context)
        {
            if (circuitOpened && successCount > 0)
            {
                context.Logger.LogWarning(
                    $"PromotionBatchPartiallyAborted | CircuitOpen | " +
                    $"Promoted: {successCount} orders to FILLED and notified subscribers | " +
                    $"Remaining {failureCount} orders stay ACKNOWLEDGED and will be retried next cycle");
            }
            else if (circuitOpened)
            {
                context.Logger.LogWarning(
                    $"PromotionBatchAborted | CircuitOpen | " +
                    $"No orders promoted | All {totalOrderCount} orders stay ACKNOWLEDGED");
            }
            else if (failureCount > 0 && successCount == 0)
            {
                context.Logger.LogWarning(
                    $"PromotionBatchFailed | TopicUnreachable | " +
                    $"No orders promoted to FILLED | Failed: {failureCount}");
            }
            else if (failureCount > 0)
            {
                context.Logger.LogWarning(
                    $"PromotionBatchPartial | " +
                    $"Promoted: {successCount} | Failed to notify: {failureCount}");
            }
            else
            {
                context.Logger.LogWarning(
                    $"PromotionBatchComplete | " +
                    $"Promoted {successCount} orders to FILLED | Subscribers notified");
            }
            //_tradingDbContext.SaveChangesAsync();
        }

        private static void SimulateTopicFailure(bool isTopicDown, ILambdaContext context)
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
    }
}
