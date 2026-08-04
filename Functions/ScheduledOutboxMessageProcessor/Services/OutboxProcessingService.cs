using Amazon.Lambda.Core;
using Amazon.SQS;
using Amazon.SQS.Model;
using Handler.Interfaces;
using Handler.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;
using System.Diagnostics;
using System.Text.Json;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.OutboxMessage;
using TradingApp.Domain.Models.Enums;
using TradingApp.Infrastructure;

namespace Handler.Services
{
    public class OutboxProcessingService : IOutboxProcessingService
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IDbContextFactory<TradingDbContext> _dbContextFactory;
        private readonly IAsyncPolicy _sqlResiliencePolicy;
        private readonly IAsyncPolicy _messagingResiliencePolicy;
        private readonly IAmazonSQS _sqsClient;
        private readonly string _createOrderQueueUrl;

        private static int _queueFailureCount = 0;
        private const int LEASE_SECONDS = 130; // timeout 120s + buffer 10s

        public OutboxProcessingService(
            TradingDbContext tradingDbContext,
            IDbContextFactory<TradingDbContext> dbContextFactory,
            [FromKeyedServices(ResiliencePolicyKey.Sql)] IAsyncPolicy sqlResiliencePolicy,
            [FromKeyedServices(ResiliencePolicyKey.Messaging)] IAsyncPolicy messagingResiliencePolicy,
            IAmazonSQS sqsClient,
            OutboxMessageProcessorSettings settings
        )
        {
            _tradingDbContext = tradingDbContext;
            _dbContextFactory = dbContextFactory;
            _sqlResiliencePolicy = sqlResiliencePolicy;
            _messagingResiliencePolicy = messagingResiliencePolicy;
            _sqsClient = sqsClient;
            _createOrderQueueUrl = settings.CreateOrderQueueUrl;
        }

        public async Task ProcessOutboxMessagesConcurrentlyAsync(ILambdaContext context, int maxDegreeOfParallelism)
        {
            var stopwatch = Stopwatch.StartNew();

            var outboxMessages = await _sqlResiliencePolicy.ExecuteAsync(async () =>
                await _tradingDbContext.OutboxMessages
                 .Where(x => x.ProcessedAt == null && x.RetryCount < 5)
                 .OrderBy(x => x.CreatedAt)
                 .Take(50)
                 .ToListAsync());

            if (outboxMessages.Count > 0)
            {
                context.Logger.LogWarning($"ProcessingPhase | Found {outboxMessages.Count} pending messages");

                var alreadyProcessedClientOrderIds = await ExtractClientOrderIdsFromAlreadyProcessedOrdersAsync(outboxMessages);

                var (successCount, failureCount, circuitOpened) = await ProcessOutboxMessagesConcurrentlyAsync(outboxMessages, alreadyProcessedClientOrderIds, context, maxDegreeOfParallelism);

                stopwatch.Stop();

                context.Logger.LogWarning(
               $"BatchProcessingTime | Mode: Concurrent | {outboxMessages.Count} outboxMessages | ElapsedTime(ms): {stopwatch.ElapsedMilliseconds}");

                GenerateLogBasedOnResults(context, successCount, failureCount, circuitOpened);
            }
            else
            {
                context.Logger.LogWarning("No outboxMessages found | nothing to process and send.");
            }
        }

        private async Task<HashSet<Guid>> ExtractClientOrderIdsFromAlreadyProcessedOrdersAsync(List<OutboxMessage> outboxMessages)
        {
            var clientOrderIds = outboxMessages
             .Where(x => Guid.TryParse(x.Payload, out _))
             .Select(x => Guid.Parse(x.Payload))
             .ToHashSet();

            var alreadyProcessedOrders = new HashSet<Guid>();

            if (clientOrderIds.Count > 0)
            {
                var processedOrders = await _sqlResiliencePolicy.ExecuteAsync(async () =>
                    await _tradingDbContext.Orders
                        .Where(x => clientOrderIds.Contains(x.ClientOrderId) && x.IsProcessed)
                        .Select(x => x.ClientOrderId)
                        .ToListAsync());

                alreadyProcessedOrders = new HashSet<Guid>(processedOrders);
            }

            return alreadyProcessedOrders;
        }

        private async Task<(int successCount, int failureCount, bool circuitOpened)> ProcessOutboxMessagesConcurrentlyAsync(
            List<OutboxMessage> outboxMessages, HashSet<Guid> alreadyProcessedClientOrderIds, ILambdaContext context, int maxDegreeOfParallelism
          )
        {
            var successCount = 0;
            var failureCount = 0;
            var circuitOpenedFlag = 0;

            using var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);

            var tasks = outboxMessages.Select(async outboxMessage =>
            {
                await semaphore.WaitAsync();

                try
                {
                    if (Volatile.Read(ref circuitOpenedFlag) == 1)
                    {
                        context.Logger.LogWarning(
                           $"SkippedCircuitOpen | CorrelationId: {outboxMessage.CorrelationId} | outboxMessage stays unprocessed.");
                        Interlocked.Increment(ref failureCount);
                        return;
                    }

                    var outcome = await ProcessAndSendOutboxMessageAsync(outboxMessage, alreadyProcessedClientOrderIds, context);

                    switch (outcome)
                    {
                        case ProcessOutboxMessageOutcome.Sent:
                            Interlocked.Increment(ref successCount);
                            break;
                        case ProcessOutboxMessageOutcome.AlreadyProcessed:
                            break;
                        case ProcessOutboxMessageOutcome.Failure:
                            Interlocked.Increment(ref failureCount);
                            break;
                        case ProcessOutboxMessageOutcome.CircuitOpen:
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

        private async Task<ProcessOutboxMessageOutcome> ProcessAndSendOutboxMessageAsync(OutboxMessage outboxMessage, HashSet<Guid> alreadyProcessedClientOrderIds, ILambdaContext context)
        {
            TradingDbContext outboxMessageDbContext;

            try
            {
                outboxMessageDbContext = await _dbContextFactory.CreateDbContextAsync();
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"DbContextCreationFailed | CorrelationId: {outboxMessage.CorrelationId} | OutboxMessage stays unprocessed and unsent | Error: {ex.Message}");
                return ProcessOutboxMessageOutcome.Failure;
            }

            await using (outboxMessageDbContext)
            {
                int claimed;

                try
                {
                    claimed = await _sqlResiliencePolicy.ExecuteAsync(async () =>
                        await outboxMessageDbContext.OutboxMessages
                            .Where(x => x.Id == outboxMessage.Id && (x.ClaimedBy == null || x.ClaimedAt < DateTimeOffset.UtcNow.AddSeconds(-LEASE_SECONDS)))
                            .ExecuteUpdateAsync(x => x
                                .SetProperty(c => c.ClaimedBy, context.AwsRequestId)
                                .SetProperty(c => c.ClaimedAt, DateTimeOffset.UtcNow)
                             ));
                }
                catch (BrokenCircuitException)
                {
                    context.Logger.LogWarning(
                        $"CircuitOpen | Database unreachable | Stopping batch | CorrelationId: {outboxMessage.CorrelationId}");
                    return ProcessOutboxMessageOutcome.CircuitOpen;
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(
                        $"ClaimFailed | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | Error: {ex.Message}");
                    return ProcessOutboxMessageOutcome.Failure;
                }

                if (claimed == 0)
                {
                    context.Logger.LogWarning(
                       $"OutboxMessageAlreadyClaimed | OutboxId: {outboxMessage.Id} | CorrelationId: {outboxMessage.CorrelationId} | Skipping - claimed by another invocation or lease still active");
                    return ProcessOutboxMessageOutcome.Failure;
                }

                if (!Guid.TryParse(outboxMessage.Payload, out var clientOrderId))
                {
                    context.Logger.LogError(
                        $"InvalidPayload | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | Payload: {outboxMessage.Payload}");

                    try
                    {
                        await _sqlResiliencePolicy.ExecuteAsync(async () =>
                            await outboxMessageDbContext.OutboxMessages
                                .Where(x => x.Id == outboxMessage.Id)
                                .ExecuteUpdateAsync(x => x
                                .SetProperty(x => x.RetryCount, (x) => x.RetryCount + 1)
                                .SetProperty(x => x.RetryReason, OutboxRetryReason.InvalidPayload)));
                    }
                    catch (Exception retryUpdateEx)
                    {
                        context.Logger.LogError(
                            $"RetryCountUpdateFailed | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | Error: {retryUpdateEx.Message}");
                    }

                    return ProcessOutboxMessageOutcome.Failure;
                }

                if (alreadyProcessedClientOrderIds.Contains(clientOrderId))
                {
                    context.Logger.LogWarning(
                        $"OrderAlreadyProcessed | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | ClientOrderId: {clientOrderId}");

                    try
                    {
                        await _sqlResiliencePolicy.ExecuteAsync(async () =>
                            await outboxMessageDbContext.OutboxMessages
                                .Where(x => x.Id == outboxMessage.Id && x.ProcessedAt == null)
                                .ExecuteUpdateAsync(x => x.SetProperty(x => x.ProcessedAt, DateTimeOffset.UtcNow)));
                    }
                    catch (BrokenCircuitException)
                    {
                        context.Logger.LogWarning(
                            $"CircuitOpen | Database unreachable | Stopping batch | CorrelationId: {outboxMessage.CorrelationId}");
                        return ProcessOutboxMessageOutcome.CircuitOpen;
                    }
                    catch (Exception ex)
                    {
                        context.Logger.LogError(
                            $"MarkProcessedAtFailed | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | Will retry next cycle | Error: {ex.Message}");
                        return ProcessOutboxMessageOutcome.Failure;
                    }

                    return ProcessOutboxMessageOutcome.AlreadyProcessed;
                }

                context.Logger.LogWarning(
                    $"SendingToQueue | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | ClientOrderId: {clientOrderId}");

                try
                {
                    await _messagingResiliencePolicy.ExecuteAsync(async () =>
                    {
                        await NotifySqsCreateOrderQueueAsync(clientOrderId, outboxMessage.CorrelationId);
                    });
                }
                catch (BrokenCircuitException)
                {
                    context.Logger.LogWarning(
                        $"CircuitOpen | Queue unreachable | Stopping batch | CorrelationId: {outboxMessage.CorrelationId} | Remaining messages will retry next cycle");
                    return ProcessOutboxMessageOutcome.CircuitOpen;
                }
                catch (AmazonSQSException sqsException)
                {
                    try
                    {
                        await _sqlResiliencePolicy.ExecuteAsync(async () =>
                            await outboxMessageDbContext.OutboxMessages
                                    .Where(x => x.Id == outboxMessage.Id)
                                    .ExecuteUpdateAsync(x => x
                                    .SetProperty(x => x.RetryCount, (x) => x.RetryCount + 1)
                                    .SetProperty(x => x.RetryReason, OutboxRetryReason.SimpleQueueServiceUnavailable)
                                    .SetProperty(x => x.LastError, sqsException.Message)));
                    }
                    catch (Exception retryUpdateEx)
                    {
                        context.Logger.LogError(
                            $"RetryCountUpdateFailed | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | Error: {retryUpdateEx.Message}");
                    }

                    context.Logger.LogError(
                        $"QueueError | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | Error: {sqsException.Message}");

                    return ProcessOutboxMessageOutcome.Failure;
                }
                catch (Exception ex)
                {
                    try
                    {
                        await _sqlResiliencePolicy.ExecuteAsync(async () =>
                            await outboxMessageDbContext.OutboxMessages
                                .Where(x => x.Id == outboxMessage.Id)
                                .ExecuteUpdateAsync(x => x
                                .SetProperty(x => x.RetryCount, (x) => x.RetryCount + 1)
                                .SetProperty(x => x.RetryReason, OutboxRetryReason.Unknown)));
                    }
                    catch (Exception retryUpdateEx)
                    {
                        context.Logger.LogError(
                            $"RetryCountUpdateFailed | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | Error: {retryUpdateEx.Message}");
                    }

                    context.Logger.LogError(
                        $"OutboxProcessingFailed | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | Error: {ex.Message}");

                    return ProcessOutboxMessageOutcome.Failure;
                }

                try
                {
                    await _sqlResiliencePolicy.ExecuteAsync(async () =>
                        await outboxMessageDbContext.OutboxMessages
                            .Where(x => x.Id == outboxMessage.Id && x.ProcessedAt == null)
                            .ExecuteUpdateAsync(x => x
                            .SetProperty(x => x.ProcessedAt, DateTimeOffset.UtcNow)));
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(
                        $"MarkProcessedAtFailed | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | " +
                        $"Message WAS sent to queue but not marked - may be re-sent next cycle | Error: {ex.Message}");
                    return ProcessOutboxMessageOutcome.Sent;
                }

                context.Logger.LogWarning(
                    $"SentToQueue | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | Queue: CREATE_ORDER_QUEUE.fifo");

                return ProcessOutboxMessageOutcome.Sent;
            }
        }

        private void GenerateLogBasedOnResults(ILambdaContext context, int successCount, int failureCount, bool circuitOpened)
        {
            if (circuitOpened)
            {
                context.Logger.LogWarning(
                    $"ProcessingBatchAborted | CircuitOpen | Succeeded: {successCount} | Failed: {failureCount} | Remaining will retry next cycle");
            }
            else if (failureCount > 0 && successCount == 0)
            {
                context.Logger.LogWarning($"ProcessingBatchFailed | All messages failed | Failed: {failureCount}");
            }
            else if (failureCount > 0)
            {
                context.Logger.LogWarning(
                    $"ProcessingBatchPartial | Succeeded: {successCount} | Failed: {failureCount}");
            }
            else
            {
                context.Logger.LogWarning($"ProcessingBatchComplete | All messages sent | Succeeded: {successCount}");
            }
        }

        private void SimulateQueueFailure(bool isQueueDown)
        {
            if (!isQueueDown) return;

            var failureCount = Interlocked.Increment(ref _queueFailureCount);

            if (failureCount <= 3)
            {
                throw new AmazonSQSException(
                    $"SIMULATED: Queue connection failed (failure {failureCount} of 3)");
            }
        }

        private async Task NotifySqsCreateOrderQueueAsync(Guid clientOrderId, string correlationId)
        {
            SimulateQueueFailure(false);

            var payload = new { ClientOrderId = clientOrderId };
            var serializedPayload = JsonSerializer.Serialize(payload);
            var request = new SendMessageRequest
            {
                QueueUrl = _createOrderQueueUrl,
                MessageBody = serializedPayload,
                MessageGroupId = clientOrderId.ToString(),
                MessageDeduplicationId = Guid.NewGuid().ToString(),
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    {
                        "CorrelationId", new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = correlationId
                        }
                     }
                }
            };

            await _sqsClient.SendMessageAsync(request);
        }
    }
}
