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
using TradingApp.Domain.Models.Entities.UnpublishedTopicMessages;
using TradingApp.Domain.Models.Enums;
using TradingApp.Events.Events;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Handler
{
    public class ScheduledUnpublishedTopicMessagesProcessor
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IDbContextFactory<TradingDbContext> _dbContextFactory;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly string _orderEventsTopicArn;
        private readonly IAsyncPolicy _sqlResiliencePolicy;
        private readonly IAsyncPolicy _messagingResiliencePolicy;

        private static int _topicFailureCount = 0;
        private const int MaxDegreeOfParallelism = 5;
        private const int LEASE_SECONDS = 130; // timeout 120s + buffer 10s
        public ScheduledUnpublishedTopicMessagesProcessor(
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

        [LambdaFunction(Timeout=120)]
        public async Task FunctionHandler(ILambdaContext context)
        {
            context.Logger.LogWarning($"ScheduledUnpublishedTopicMessagesProcessor triggered at: {DateTimeOffset.UtcNow}");

            var unpublishedMessages = await _sqlResiliencePolicy.ExecuteAsync(async () =>
                await _tradingDbContext.UnpublishedTopicMessages
                    .Where(x => x.PublishedAt == null && x.RetryCount < 5)
                    .OrderBy(x => x.CreatedAt)
                    .ThenBy(x => x.OrderStatus)
                    .Take(50)
                    .ToListAsync());

            if (unpublishedMessages.Count == 0)
            {
                context.Logger.LogWarning("NoUnpublishedMessages | No messages to retry");
                return;
            }

            context.Logger.LogWarning($"RetryingUnpublishedMessages | Found {unpublishedMessages.Count} messages to retry");
            var stopwatch = Stopwatch.StartNew();

            var (successCount, failureCount, circuitOpened) = await PublishUnpublishedMessagesConcurrentlyAsync(unpublishedMessages, context);

            stopwatch.Stop();
            context.Logger.LogWarning(
                $"BatchProcessingTime | Mode: Concurrent | {unpublishedMessages.Count} unpublishedMessages | ElapsedTime(ms): {stopwatch.ElapsedMilliseconds}");

            GenerateLogBasedOnResults(successCount, failureCount, circuitOpened, context);
        }
        private async Task<(int successCount, int failureCount, bool circuitOpened)> PublishUnpublishedMessagesConcurrentlyAsync(
            List<UnpublishedTopicMessage> unpublishedMessages, ILambdaContext context)
        {
            var successCount = 0;
            var failureCount = 0;
            var circuitOpenedFlag = 0; // 0 = false, 1 = true - Interlocked/Volatile work on int, not bool

            using var semaphore = new SemaphoreSlim(MaxDegreeOfParallelism);

            var tasks = unpublishedMessages.Select(async unpublishedMessage =>
            {
                await semaphore.WaitAsync();

                try 
                { 
                    if(Volatile.Read(ref circuitOpenedFlag) == 1)
                    {
                        context.Logger.LogWarning(
                           $"SkippedCircuitOpen | CorrelationId: {unpublishedMessage.CorrelationId} | Unpublished message stays unpublished");
                        Interlocked.Increment(ref failureCount);
                        return;
                    }

                    var outcome = await TryPublishUnpublishedMessageAsync(unpublishedMessage, context);

                    switch (outcome)
                    {
                        case ProcessUnpublishedMessageOutcome.PublishSuccess:
                            Interlocked.Increment(ref successCount);
                            break;
                        case ProcessUnpublishedMessageOutcome.PublishFailed:
                            Interlocked.Increment(ref failureCount);
                            break;
                        case ProcessUnpublishedMessageOutcome.CircuitOpen:
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

            return (successCount, failureCount, circuitOpenedFlag == 1);
        }

        private async Task<ProcessUnpublishedMessageOutcome> TryPublishUnpublishedMessageAsync(UnpublishedTopicMessage unpublishedMessage, ILambdaContext context)
        {
            TradingDbContext unpublishedMessageDbContext;

            try
            {
                unpublishedMessageDbContext = await _dbContextFactory.CreateDbContextAsync();
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"DbContextCreationFailed | CorrelationId: {unpublishedMessage.CorrelationId} | Unpublished message stays unpublished | Error: {ex.Message}");
                return ProcessUnpublishedMessageOutcome.PublishFailed;
            }

            await using (unpublishedMessageDbContext)
            {
                int claimed;

                try
                {
                    claimed = await _sqlResiliencePolicy.ExecuteAsync(async () =>
                        await unpublishedMessageDbContext.UnpublishedTopicMessages
                           .Where(x => x.Id == unpublishedMessage.Id && (x.ClaimedBy == null || x.ClaimedAt < DateTimeOffset.UtcNow.AddSeconds(-LEASE_SECONDS)))
                           .ExecuteUpdateAsync(x => x
                               .SetProperty(c => c.ClaimedBy, context.AwsRequestId)
                               .SetProperty(c => c.ClaimedAt, DateTimeOffset.UtcNow)
                            ));
                }
                catch (BrokenCircuitException)
                {
                    context.Logger.LogWarning(
                        $"CircuitOpen | Database unreachable | Stopping retry batch | CorrelationId: {unpublishedMessage.CorrelationId}");

                    return ProcessUnpublishedMessageOutcome.CircuitOpen;
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(
                        $"ClaimFailed | CorrelationId: {unpublishedMessage.CorrelationId} | UnpublishedId: {unpublishedMessage.Id} | Error: {ex.Message}");

                    return ProcessUnpublishedMessageOutcome.PublishFailed;
                }

                if (claimed == 0)
                {
                    context.Logger.LogWarning(
                       $"UnpublishedMessageAlreadyClaimed | UnpublishedId: {unpublishedMessage.Id} | CorrelationId: {unpublishedMessage.CorrelationId} | Skipping - claimed by another invocation or lease still active");
                    return ProcessUnpublishedMessageOutcome.PublishFailed;
                }

                context.Logger.LogWarning(
                      $"Trying to publish unpublishedTopicMessage | CorrelationId: {unpublishedMessage.CorrelationId} | UnpublishedId: {unpublishedMessage.Id} " +
                      $"| ClientOrderId: {unpublishedMessage.ClientOrderId}");

                var eventPayload = new OrderStatusChangedEvent
                {
                    ClientOrderId = unpublishedMessage.ClientOrderId,
                    Status = unpublishedMessage.OrderStatus.ToString(),
                    EventTime = unpublishedMessage.ProcessedAt,
                    Sequence = unpublishedMessage.OrderStatus == OrderStatus.FILLED ? 2 : 1,
                    CorrelationId = unpublishedMessage.CorrelationId
                };

                var messageBody = JsonSerializer.Serialize(eventPayload);

                var request = new PublishRequest
                {
                    TopicArn = _orderEventsTopicArn,
                    Message = messageBody,
                    Subject = "OrderProcessed",
                    MessageGroupId = unpublishedMessage.ClientOrderId.ToString(),
                    MessageDeduplicationId = Guid.NewGuid().ToString()
                };

                try
                {
                    await _messagingResiliencePolicy.ExecuteAsync(async () =>
                    {
                        SimulateTopicFailure(false, context);
                        await _snsClient.PublishAsync(request);
                    });
                }
                catch (BrokenCircuitException)
                {
                    context.Logger.LogWarning(
                        $"CircuitOpen | Topic circuit open | Stopping retry batch | CorrelationId: {unpublishedMessage.CorrelationId}");

                    return ProcessUnpublishedMessageOutcome.CircuitOpen;
                }
                catch (AmazonSimpleNotificationServiceException snsEx)
                {
                    try
                    {
                        await _sqlResiliencePolicy.ExecuteAsync(async () =>
                            await unpublishedMessageDbContext.UnpublishedTopicMessages
                                    .Where(x => x.Id == unpublishedMessage.Id)
                                    .ExecuteUpdateAsync(x => x
                                        .SetProperty(x => x.RetryCount, (x) => x.RetryCount + 1)
                                        .SetProperty(x => x.LastError, snsEx.Message)));
                    }
                    catch (Exception retryUpdateEx)
                    {
                        context.Logger.LogError(
                            $"RetryCountUpdateFailed | CorrelationId: {unpublishedMessage.CorrelationId} | UnpublishedId: {unpublishedMessage.Id} | Error: {retryUpdateEx.Message}");
                    }

                    context.Logger.LogError(
                        $"TopicPublishRetryFailed | CorrelationId: {unpublishedMessage.CorrelationId} " +
                        $"| UnpublishedId: {unpublishedMessage.Id} | Error: {snsEx.Message}");

                    return ProcessUnpublishedMessageOutcome.PublishFailed;
                }
                catch (Exception ex)
                {
                    try
                    {
                        await _sqlResiliencePolicy.ExecuteAsync(async () =>
                            await unpublishedMessageDbContext.UnpublishedTopicMessages
                                  .Where(x => x.Id == unpublishedMessage.Id)
                                  .ExecuteUpdateAsync(x => x
                                      .SetProperty(x => x.RetryCount, (x) => x.RetryCount + 1)));
                    }
                    catch (Exception retryUpdateEx)
                    {
                        context.Logger.LogError(
                            $"RetryCountUpdateFailed | CorrelationId: {unpublishedMessage.CorrelationId} | UnpublishedId: {unpublishedMessage.Id} | Error: {retryUpdateEx.Message}");
                    }

                    context.Logger.LogError(
                        $"TopicPublishRetryFailed | CorrelationId: {unpublishedMessage.CorrelationId} " +
                        $"| UnpublishedId: {unpublishedMessage.Id} | Error: {ex.Message}");

                    return ProcessUnpublishedMessageOutcome.PublishFailed;
                }

                try
                {
                    await _sqlResiliencePolicy.ExecuteAsync(async () =>
                        await unpublishedMessageDbContext.UnpublishedTopicMessages
                                .Where(x => x.Id == unpublishedMessage.Id && x.PublishedAt == null)
                                .ExecuteUpdateAsync(x => x
                                    .SetProperty(x => x.PublishedAt, DateTimeOffset.UtcNow)));
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(
                        $"MarkPublishedAtFailed | CorrelationId: {unpublishedMessage.CorrelationId} | UnpublishedId: {unpublishedMessage.Id} | " +
                        $"Event WAS published to SNS but not marked - may be re-published next cycle | Error: {ex.Message}");

                    return ProcessUnpublishedMessageOutcome.PublishSuccess;
                }

                context.Logger.LogWarning(
                    $"TopicPublishMessageRetrySucceeded | CorrelationId: {unpublishedMessage.CorrelationId} | UnpublishedId: {unpublishedMessage.Id}");

                return ProcessUnpublishedMessageOutcome.PublishSuccess;
            }
        }

        private static void GenerateLogBasedOnResults(int successCount, int failureCount, bool circuitOpened, ILambdaContext context)
        {
            if (circuitOpened)
            {
                context.Logger.LogWarning(
                    $"RetryBatchAborted | CircuitOpen | Succeeded: {successCount} | Failed: {failureCount} | Remaining will retry next cycle");
            }
            else if (failureCount > 0 && successCount == 0)
            {
                context.Logger.LogWarning($"RetryBatchFailed | All messages failed | Failed: {failureCount}");
            }
            else if (failureCount > 0)
            {
                context.Logger.LogWarning($"RetryBatchPartial | Succeeded: {successCount} | Failed: {failureCount}");
            }
            else
            {
                context.Logger.LogWarning($"RetryProcessingComplete | All messages published | Succeeded: {successCount}");
            }
        }

        private static void SimulateTopicFailure(bool isTopicDown, ILambdaContext context)
        {
            if (!isTopicDown) return;

            var failureCount = Interlocked.Increment(ref _topicFailureCount);

            if (failureCount <= 3)
            {
                context.Logger.LogWarning($"SIMULATION | Simulating topic outage | FailureCount: {failureCount}");

                throw new InternalErrorException(
                    $"SIMULATED: Topic connection failed (failure {failureCount} of 3)");
            }
        }
    }
}
