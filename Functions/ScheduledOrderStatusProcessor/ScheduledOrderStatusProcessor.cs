using Amazon.Lambda.Annotations;
using Amazon.Lambda.Core;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;
using System.Diagnostics;
using System.Text.Json;
using TradingApp.Domain;
using TradingApp.Infrastructure;
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
        private readonly IAsyncPolicy _sqlResiliencePolicy;
        private readonly IAsyncPolicy _messagingResiliencePolicy;
        private static int _topicFailureCount = 0;

        public ScheduledOrderStatusProcessor(
            TradingDbContext tradingDbContext,
            IDbContextFactory<TradingDbContext> dbContextFactory,
            IAmazonSimpleNotificationService snsClient,
            [FromKeyedServices(ResiliencePolicyKey.Sql)] IAsyncPolicy sqlResiliencePolicy,
            [FromKeyedServices(ResiliencePolicyKey.Messaging)] IAsyncPolicy messagingResiliencePolicy)
        {
            _tradingDbContext = tradingDbContext;
            _dbContextFactory = dbContextFactory;
            _snsClient = snsClient;
            _sqlResiliencePolicy = sqlResiliencePolicy;
            _messagingResiliencePolicy = messagingResiliencePolicy;

            _orderEventsTopicArn = Environment.GetEnvironmentVariable("ORDER_EVENTS_TOPIC_ARN")
                ?? throw new InvalidOperationException("ORDER_EVENTS_TOPIC_ARN environment variable is not set.");
        }

        [LambdaFunction]
        public async Task FunctionHandler(ILambdaContext context)
        {
            context.Logger.LogWarning($"ScheduledOrderStatusProcessor triggered at: {DateTimeOffset.UtcNow}");

            var orders = await _sqlResiliencePolicy.ExecuteAsync(async () =>
                await _tradingDbContext.Orders
                    .Where(ao => ao.Status == OrderStatus.ACKNOWLEDGED)
                    .OrderBy(x => x.CreatedAt)
                    .Take(50)
                    .ToListAsync()
                );

            if (orders.Count == 0)
            {
                context.Logger.LogWarning("NoAcknowledgedOrders | No orders to promote to FILLED");
                return;
            }

            context.Logger.LogWarning($"PromotingOrders | Found {orders.Count} ACKNOWLEDGED orders to promote");

            var stopwatch = Stopwatch.StartNew();

            var (filledAndPublished, filledPublishDeferred, filledButNotSavedNorPublished, saveFailed, sqlCircuitOpened) = UseConcurrentProcessing
                ? await PromoteOrdersConcurrentlyAsync(orders, context)
                : await PromoteOrdersSequentiallyAsync(orders, context);

            stopwatch.Stop();
            context.Logger.LogWarning(
                $"BatchProcessingTime | Mode: {(UseConcurrentProcessing ? "Concurrent" : "Sequential")} " +
                $"| {orders.Count} orders | ElapsedTime(ms): {stopwatch.ElapsedMilliseconds}");

            GenerateLogBasedOnResults(filledAndPublished, filledPublishDeferred, filledButNotSavedNorPublished, saveFailed, sqlCircuitOpened, orders.Count, context);
        }

        private async Task<(int filledAndPublished, int filledPublishDeferred, int filledButNotSavedNorPublished, int saveFailed, bool sqlCircuitOpened)> PromoteOrdersSequentiallyAsync(
            List<Order> orders, ILambdaContext context)
        {
            var filledAndPublished = 0;
            var filledPublishDeferred = 0;
            var filledButNotSavedNorPublished = 0;
            var saveFailed = 0;
            var sqlCircuitOpened = false;

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
                    case ProcessedOrderStatusOutcome.FilledButNotSavedNorPublished:
                        filledButNotSavedNorPublished++;
                        break;
                    case ProcessedOrderStatusOutcome.SaveFailed:
                        saveFailed++;
                        break;
                    case ProcessedOrderStatusOutcome.SqlCircuitOpen:
                        sqlCircuitOpened = true;
                        break;
                }

                if (sqlCircuitOpened)
                {
                    break;
                }
            }

            return (filledAndPublished, filledPublishDeferred, filledButNotSavedNorPublished, saveFailed, sqlCircuitOpened);
        }

        private async Task<(int filledAndPublished, int filledPublishDeferred, int filledButNotSavedNorPublished, int saveFailed, bool sqlCircuitOpened)> PromoteOrdersConcurrentlyAsync(
            List<Order> orders, ILambdaContext context)
        {
            var filledAndPublished = 0;
            var filledPublishDeferred = 0;
            var filledButNotSavedNorPublished = 0;
            var saveFailed = 0;
            var sqlCircuitOpenedFlag = 0; // 0 = false, 1 = true - Interlocked/Volatile work on int, not bool

            using var semaphore = new SemaphoreSlim(MaxDegreeOfParallelism);

            var tasks = orders.Select(async order =>
            {
                await semaphore.WaitAsync();

                try
                {
                    if (Volatile.Read(ref sqlCircuitOpenedFlag) == 1)
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
                        case ProcessedOrderStatusOutcome.FilledButNotSavedNorPublished:
                            Interlocked.Increment(ref filledButNotSavedNorPublished);
                            break;
                        case ProcessedOrderStatusOutcome.SaveFailed:
                            Interlocked.Increment(ref saveFailed);
                            break;
                        case ProcessedOrderStatusOutcome.SqlCircuitOpen:
                            Interlocked.Exchange(ref sqlCircuitOpenedFlag, 1);
                            break;
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            return (filledAndPublished, filledPublishDeferred, filledButNotSavedNorPublished, saveFailed, sqlCircuitOpenedFlag == 1);
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
                    var rowsUpdated = await _sqlResiliencePolicy.ExecuteAsync(async () =>
                            await orderrDbContext.Orders
                                .Where(x => x.Id == order.Id && x.Status == OrderStatus.ACKNOWLEDGED)
                                .ExecuteUpdateAsync(x => x
                                    .SetProperty(x => x.Status, OrderStatus.FILLED)
                                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow))
                            );

                    if (rowsUpdated == 0)
                    {
                        context.Logger.LogWarning(
                            $"OrderAlreadyProcessed | CorrelationId: {order.CorrelationId} | ClientOrderId: {order.ClientOrderId}");
                        return ProcessedOrderStatusOutcome.SaveFailed;
                    }
                }
                catch (BrokenCircuitException)
                {
                    context.Logger.LogWarning(
                        $"CircuitOpen | CorrelationId: {order.CorrelationId} | Order stays ACKNOWLEDGED | Database unreachable, stopping batch");
                    return ProcessedOrderStatusOutcome.SqlCircuitOpen;
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(
                        $"OrderStatusSaveFailed | CorrelationId: {order.CorrelationId} | Order stays ACKNOWLEDGED | Error: {ex.Message}");
                    return ProcessedOrderStatusOutcome.SaveFailed;
                }

                // From here on, the order IS FILLED in the database - only the event publish can still fail.
                var eventPayload = new OrderStatusChangedEvent
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
                    await _messagingResiliencePolicy.ExecuteAsync(async () =>
                    {
                        SimulateTopicFailure(true, context);
                        await _snsClient.PublishAsync(request);
                    });

                    context.Logger.LogWarning(
                        $"OrderFilledAndPublished | CorrelationId: {order.CorrelationId} | ClientOrderId: {order.ClientOrderId}");
                    return ProcessedOrderStatusOutcome.Filled;
                }
                catch (BrokenCircuitException)
                {
                    context.Logger.LogWarning(
                        $"CircuitOpen | CorrelationId: {order.CorrelationId} | event not yet published | attempting to persist to UnpublishedTopicMessages");

                    var outcome = await TryPersistUnpublishedTopicMessageAsync(context, order);

                    return outcome;
                }
                catch (AmazonSimpleNotificationServiceException snsEx)
                {
                    context.Logger.LogError(
                        $"TopicPublishFailed | CorrelationId: {order.CorrelationId} | Falling back to UnpublishedTopicMessages | Error: {snsEx.Message}");
                 
                    return await TryPersistUnpublishedTopicMessageAsync(context, order);
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
                int filledAndPublished, int filledPublishDeferred, int filledButNotSavedNorPublished, int saveFailed, bool circuitOpened, int totalOrderCount, ILambdaContext context
            )
        {
            var promotedCount = filledAndPublished + filledPublishDeferred + filledButNotSavedNorPublished;

            if (filledButNotSavedNorPublished > 0)
            {
                context.Logger.LogError(
                    $"FilledButNotSavedNorPublished | {filledButNotSavedNorPublished} orders are FILLED but their event was neither published nor saved to UnpublishedTopicMessages | Needs manual intervention.");
            }

            if (circuitOpened)
            {
                context.Logger.LogWarning(
                    $"PromotionBatchAborted | CircuitOpen | " +
                    $"Promoted: {promotedCount} orders to FILLED ({filledAndPublished} published, {filledPublishDeferred} deferred, {filledButNotSavedNorPublished} not saved/not published) | " +
                    $"SaveFailed: {saveFailed} orders stay ACKNOWLEDGED | Remaining orders not attempted, will retry next cycle");
            }
            else if (saveFailed > 0 && promotedCount == 0)
            {
                context.Logger.LogWarning(
                    $"PromotionBatchFailed | No orders promoted | SaveFailed: {saveFailed}");
            }
            else if (saveFailed > 0 || filledPublishDeferred > 0 || filledButNotSavedNorPublished > 0)
            {
                context.Logger.LogWarning(
                    $"PromotionBatchPartial | " +
                    $"Promoted: {promotedCount} ({filledAndPublished} published, {filledPublishDeferred} deferred, {filledButNotSavedNorPublished} not saved/not published) | " +
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

        private async Task<ProcessedOrderStatusOutcome> TryPersistUnpublishedTopicMessageAsync(ILambdaContext context, Order order)
        {
            try
            {
                var outcome = await _sqlResiliencePolicy.ExecuteAsync(async Task<ProcessedOrderStatusOutcome> () =>
                {
                   
                    var alreadyExists = await _tradingDbContext.CheckIfUnpublishedTopicMessageExistsAsync(
                        order.ClientOrderId, OrderStatus.FILLED, order.CorrelationId);

                    if (alreadyExists == null)
                    {
                        await _tradingDbContext.SaveUnpublishedTopicMessagesAsync(order.ClientOrderId, OrderStatus.FILLED, order.CorrelationId);

                        context.Logger.LogWarning(
                    $"SavedToUnpublishedTopicMessages | CorrelationId: {order.CorrelationId} | ClientOrderId: {order.ClientOrderId}");

                        return ProcessedOrderStatusOutcome.FilledPublishDeferred;
                    }
                    else
                    {
                        context.Logger.LogWarning(
                              $"UnpublishedTopicMessageAlreadyExists | CorrelationId: {order.CorrelationId} | ClientOrderId: {order.ClientOrderId}");
                        return ProcessedOrderStatusOutcome.FilledPublishDeferred;
                    }
                  
                });

                return outcome;
            }
            catch (BrokenCircuitException)
            {
                context.Logger.LogWarning(
                        $"CircuitOpen | FailedPersistingToUnpublishedTopicMessages | CorrelationId: {order.CorrelationId} | Database unreachable, stopping batch");
                return ProcessedOrderStatusOutcome.SqlCircuitOpen;
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
               $"UnexpectedError | CorrelationId: {order.CorrelationId} | UnpublishedTopicMessagePersisted, event not published | Error: {ex.Message}");

                return ProcessedOrderStatusOutcome.FilledButNotSavedNorPublished;
            }
        }
    }
}
