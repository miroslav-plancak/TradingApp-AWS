using Amazon.Lambda.Annotations;
using Amazon.Lambda.Core;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.EntityFrameworkCore;
using Polly.CircuitBreaker;
using System.Diagnostics;
using System.Text.Json;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.Order;
using TradingApp.Domain.Models.Enums;
using TradingApp.Events.Events;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Handler
{
    public class ScheduledOrderStatusProcessor
    {
        private const bool UseConcurrentProcessing = false;
        private const int MaxDegreeOfParallelism = 5;

        private readonly TradingDbContext _tradingDbContext;
        private readonly IDbContextFactory<TradingDbContext> _dbContextFactory;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly string _orderEventsTopicArn;
        private readonly AsyncCircuitBreakerPolicy _circuitBreaker;
        private static int _topicFailureCount = 0;

        public ScheduledOrderStatusProcessor(
            TradingDbContext tradingDbContext,
            IDbContextFactory<TradingDbContext> dbContextFactory,
            IAmazonSimpleNotificationService snsClient,
            AsyncCircuitBreakerPolicy circuitBreaker)
        {
            _tradingDbContext = tradingDbContext;
            _dbContextFactory = dbContextFactory;
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

            var stopwatch = Stopwatch.StartNew();

            var (filledAndPublished, filledPublishDeferred, saveFailed, circuitOpened) = UseConcurrentProcessing
                ? await PromoteOrdersConcurrentlyAsync(orders, context)
                : await PromoteOrdersSequentiallyAsync(orders, context);

            stopwatch.Stop();
            context.Logger.LogWarning(
                $"BatchProcessingTime | Mode: {(UseConcurrentProcessing ? "Concurrent" : "Sequential")} " +
                $"| {orders.Count} orders | ElapsedTime(ms): {stopwatch.ElapsedMilliseconds}");

            GenerateLogBasedOnResults(filledAndPublished, filledPublishDeferred, saveFailed, circuitOpened, orders.Count, context);
        }

        private async Task<(int filledAndPublished, int filledPublishDeferred, int saveFailed, bool circuitOpened)> PromoteOrdersSequentiallyAsync(
            List<Order> orders, ILambdaContext context)
        {
            var filledAndPublished = 0;
            var filledPublishDeferred = 0;
            var saveFailed = 0;
            var circuitOpened = false;

            foreach (var order in orders)
            {
                var outcome = await TryPromoteOrderAsync(order, context);

                switch (outcome)
                {
                    case ProcessedOrderStatusOutcome.Filled:
                        filledAndPublished++;
                        break;
                    case ProcessedOrderStatusOutcome.FilledPublishDeferred:
                        filledPublishDeferred++;
                        break;
                    case ProcessedOrderStatusOutcome.SaveFailed:
                        saveFailed++;
                        break;
                    case ProcessedOrderStatusOutcome.CircuitOpen:
                        circuitOpened = true;
                        break;
                }

                // Sequential can stop cleanly the moment it discovers the circuit is open -
                // nothing after this point in the loop has started yet.
                if (circuitOpened)
                {
                    break;
                }
            }

            return (filledAndPublished, filledPublishDeferred, saveFailed, circuitOpened);
        }

        private async Task<(int filledAndPublished, int filledPublishDeferred, int saveFailed, bool circuitOpened)> PromoteOrdersConcurrentlyAsync(
            List<Order> orders, ILambdaContext context)
        {
            var filledAndPublished = 0;
            var filledPublishDeferred = 0;
            var saveFailed = 0;
            var circuitOpenedFlag = 0; // 0 = false, 1 = true - Interlocked/Volatile work on int, not bool

            using var semaphore = new SemaphoreSlim(MaxDegreeOfParallelism);

            var tasks = orders.Select(async order =>
            {
                await semaphore.WaitAsync();

                try
                {
                    if (Volatile.Read(ref circuitOpenedFlag) == 1)
                    {
                        context.Logger.LogWarning(
                            $"SkippedCircuitOpen | CorrelationId: {order.CorrelationId} | Order stays ACKNOWLEDGED");
                        Interlocked.Increment(ref saveFailed);
                        return;
                    }

                    var outcome = await TryPromoteOrderAsync(order, context);

                    switch (outcome)
                    {
                        case ProcessedOrderStatusOutcome.Filled:
                            Interlocked.Increment(ref filledAndPublished);
                            break;
                        case ProcessedOrderStatusOutcome.FilledPublishDeferred:
                            Interlocked.Increment(ref filledPublishDeferred);
                            break;
                        case ProcessedOrderStatusOutcome.SaveFailed:
                            Interlocked.Increment(ref saveFailed);
                            break;
                        case ProcessedOrderStatusOutcome.CircuitOpen:
                            Interlocked.Exchange(ref circuitOpenedFlag, 1);
                            break;
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });
   
            await Task.WhenAll(tasks);

            return (filledAndPublished, filledPublishDeferred, saveFailed, circuitOpenedFlag == 1);
        }


        // One order, start to finish: save first, only publish after the saving is confirmed.
        // Uses its own TradingDbContext (from the factory) so this is safe to call from concurrent tasks as well.
        private async Task<ProcessedOrderStatusOutcome> TryPromoteOrderAsync(Order order, ILambdaContext context)
        {
            TradingDbContext orderrDbContext;

            try
            {
                orderrDbContext = await _dbContextFactory.CreateDbContextAsync();
            }
            catch(Exception ex)
            {
                context.Logger.LogError(
                    $"DbContextCreationFailed | CorrelationId: {order.CorrelationId} | Order stays ACKNOWLEDGED | Error: {ex.Message}");
                return ProcessedOrderStatusOutcome.SaveFailed;
            }

            await using (orderrDbContext) { 

                context.Logger.LogWarning(
                    $"Trying to promote order | CorrelationId: {order.CorrelationId} | OrderId: {order.Id} " +
                    $"| ClientOrderId: {order.ClientOrderId} | ACKNOWLEDGED => FILLED");

                try
                {
                    var rowsUpdated = await orderrDbContext.Orders
                        .Where(x => x.Id == order.Id && x.Status == OrderStatus.ACKNOWLEDGED)
                        .ExecuteUpdateAsync(x => x
                            .SetProperty(x => x.Status, OrderStatus.FILLED)
                            .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow));

                    if (rowsUpdated == 0)
                    {
                        context.Logger.LogWarning(
                            $"OrderAlreadyProcessed | CorrelationId: {order.CorrelationId} | ClientOrderId: {order.ClientOrderId}");
                        return ProcessedOrderStatusOutcome.SaveFailed;
                    }
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(
                        $"OrderStatusSaveFailed | CorrelationId: {order.CorrelationId} | Order stays ACKNOWLEDGED | Error: {ex.Message}");
                    return ProcessedOrderStatusOutcome.SaveFailed;
                }

                // From here on, the order IS FILLED in the database - only the event publish can still fail.
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

                try
                {
                    await _circuitBreaker.ExecuteAsync(async () =>
                    {
                        SimulateTopicFailure(false, context);
                        await _snsClient.PublishAsync(request);
                    });

                    context.Logger.LogWarning(
                        $"OrderFilledAndPublished | CorrelationId: {order.CorrelationId} | ClientOrderId: {order.ClientOrderId}");
                    return ProcessedOrderStatusOutcome.Filled;
                }
                catch (BrokenCircuitException)
                {
                    context.Logger.LogWarning(
                        $"CircuitOpen | CorrelationId: {order.CorrelationId} | Order is FILLED, event not yet published | Falling back to UnpublishedTopicMessages");

                    await _tradingDbContext.SaveUnpublishedTopicMessagesAsync(order.ClientOrderId, OrderStatus.FILLED, order.CorrelationId);

                    context.Logger.LogWarning(
                        $"SavedToUnpublishedTopicMessages | CorrelationId: {order.CorrelationId} | ClientOrderId: {order.ClientOrderId}");

                    return ProcessedOrderStatusOutcome.CircuitOpen;
                }
                catch (AmazonSimpleNotificationServiceException snsEx)
                {
                    context.Logger.LogError(
                        $"TopicPublishFailed | CorrelationId: {order.CorrelationId} | Falling back to UnpublishedTopicMessages | Error: {snsEx.Message}");

                    await _tradingDbContext.SaveUnpublishedTopicMessagesAsync(order.ClientOrderId, OrderStatus.FILLED, order.CorrelationId);

                    context.Logger.LogWarning(
                        $"SavedToUnpublishedTopicMessages | CorrelationId: {order.CorrelationId} | ClientOrderId: {order.ClientOrderId}");

                    return ProcessedOrderStatusOutcome.FilledPublishDeferred;
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(
                        $"UnexpectedError | CorrelationId: {order.CorrelationId} | Order is FILLED, event not published | Error: {ex.Message}");
                    return ProcessedOrderStatusOutcome.FilledPublishDeferred;
                }
            }
        }

        private static void GenerateLogBasedOnResults(
                int filledAndPublished, int filledPublishDeferred, int saveFailed, bool circuitOpened, int totalOrderCount, ILambdaContext context
            )
        {
            var promotedCount = filledAndPublished + filledPublishDeferred;

            if (circuitOpened)
            {
                context.Logger.LogWarning(
                    $"PromotionBatchAborted | CircuitOpen | " +
                    $"Promoted: {promotedCount} orders to FILLED ({filledAndPublished} published, {filledPublishDeferred} deferred) | " +
                    $"SaveFailed: {saveFailed} orders stay ACKNOWLEDGED | Remaining orders not attempted, will retry next cycle");
            }
            else if (saveFailed > 0 && promotedCount == 0)
            {
                context.Logger.LogWarning(
                    $"PromotionBatchFailed | No orders promoted | SaveFailed: {saveFailed}");
            }
            else if (saveFailed > 0 || filledPublishDeferred > 0)
            {
                context.Logger.LogWarning(
                    $"PromotionBatchPartial | " +
                    $"Promoted: {promotedCount} ({filledAndPublished} published, {filledPublishDeferred} deferred) | " +
                    $"SaveFailed: {saveFailed}");
            }
            else
            {
                context.Logger.LogWarning(
                    $"PromotionBatchComplete | Promoted {filledAndPublished} orders to FILLED | Subscribers notified");
            }
        }

        private static void SimulateTopicFailure(bool isTopicDown, ILambdaContext context)
        {
            if (!isTopicDown) return;

            var failureCount = Interlocked.Increment(ref _topicFailureCount);

            if (failureCount <= 3)
            {
                context.Logger.LogWarning(
                    $"SIMULATION | Simulating topic outage | FailureCount: {failureCount}");

                throw new InternalErrorException(
                    $"SIMULATED: Topic connection failed (failure {failureCount} of 3)");
            }
        }
    }
}
