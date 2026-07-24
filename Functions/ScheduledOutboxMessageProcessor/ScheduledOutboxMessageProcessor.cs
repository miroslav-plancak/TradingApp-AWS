using Amazon.Lambda.Annotations;
using Amazon.Lambda.Core;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using Polly.CircuitBreaker;
using System.Diagnostics;
using System.Text.Json;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.OutboxMessage;
using TradingApp.Domain.Models.Entities.QuarantinedOutboxMessage;
using TradingApp.Domain.Models.Enums;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Handler
{
    public class ScheduledOutboxMessageProcessor
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IDbContextFactory<TradingDbContext> _dbContextFactory;
        private readonly IAmazonSQS _sqsClient;
        private readonly string _createOrderQueueUrl;
        private readonly AsyncCircuitBreakerPolicy _circuitBreaker;

        private static int _queueFailureCount = 0;
        private const int MaxDegreeOfParallelism = 5;

        public ScheduledOutboxMessageProcessor(
            TradingDbContext tradingDbContext,
            IDbContextFactory<TradingDbContext> dbContextFactory,
            IAmazonSQS sqsClient,
            AsyncCircuitBreakerPolicy circuitBreaker)
        {
            _tradingDbContext = tradingDbContext;
            _dbContextFactory = dbContextFactory;
            _sqsClient = sqsClient;
            _circuitBreaker = circuitBreaker;

            _createOrderQueueUrl = Environment.GetEnvironmentVariable("CREATE_ORDER_QUEUE_URL")
                ?? throw new InvalidOperationException("CREATE_ORDER_QUEUE_URL environment variable is not set.");
        }

        [LambdaFunction]
        public async Task FunctionHandler(ILambdaContext context)
        {
            context.Logger.LogWarning($"ScheduledOutboxMessageProcessor triggered at: {DateTimeOffset.UtcNow}");

            await QuarantineExhaustedMessagesAsync(context);

            var isQueueReachable = await IsQueueReachableAsync(context);

            if (isQueueReachable)
            {
                var stopwatch = Stopwatch.StartNew();

                var outboxMessages = await _tradingDbContext.OutboxMessages
                 .Where(x => x.ProcessedAt == null && x.RetryCount < 5)
                 .OrderBy(x => x.CreatedAt)
                 .Take(50)
                 .ToListAsync();

                if (outboxMessages.Count > 0)
                {
                    context.Logger.LogWarning($"ProcessingPhase | Found {outboxMessages.Count} pending messages");

                    var alreadyProcessedClientOrderIds =  await ExtractClientOrderIdsFromAlreadyProcessedOrdersAsync(outboxMessages);

                    var (successCount, failureCount, circuitOpened) = await ProcessOutboxMessagesConcurrentlyAsync(outboxMessages, alreadyProcessedClientOrderIds, context);

                    stopwatch.Stop();

                    context.Logger.LogWarning(
                   $"BatchProcessingTime | Mode: Concurrent | {outboxMessages.Count} outboxMessages | ElapsedTime(ms): {stopwatch.ElapsedMilliseconds}");

                    GenerateLogBasedOnResults(context, successCount, failureCount, circuitOpened);
                }
                else
                {
                    context.Logger.LogWarning("No outboxMessages found | nothing to process and send.");
                }
               
                await AutoRecoverResurrectedMessagesAsync(context);
            }
            else
            {
                context.Logger.LogWarning(
                    "QueueDown | Skipping ProcessPendingMessages() and AutoRecoverResurrectedMessagesAsync() this cycle.");
            }
        }

        private async Task QuarantineExhaustedMessagesAsync(ILambdaContext context)
        {
            var exhaustedOutboxMessages = await _tradingDbContext.OutboxMessages
                .Where(x => x.ProcessedAt == null && x.RetryCount >= 5)
                .OrderBy(x => x.CreatedAt)
                .Take(50)
                .ToListAsync();

            if (exhaustedOutboxMessages.Count == 0)
            {
                context.Logger.LogWarning($"QuarantinePhaseSkipped | no exhausted messages found.");
                return;
            }

            context.Logger.LogWarning($"QuarantinePhase | Found {exhaustedOutboxMessages.Count} exhausted messages");

            using var semaphore = new SemaphoreSlim(MaxDegreeOfParallelism);

            var tasks = exhaustedOutboxMessages.Select(async exObMsg =>
            {
                await semaphore.WaitAsync();

                try
                {
                    await QuarantineExhaustedMessageAsync(exObMsg, context);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }

        private async Task QuarantineExhaustedMessageAsync(OutboxMessage exObMsg, ILambdaContext context)
        {
            TradingDbContext exObMsgDbContext;

            try
            {
                exObMsgDbContext = await _dbContextFactory.CreateDbContextAsync();
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"DbContextCreationFailed | Error: {ex.Message}");
                return;
            }

            await using (exObMsgDbContext)
            {
                try
                {
                    Guid? clientOrderId = Guid.TryParse(exObMsg.Payload, out var parsed) ? parsed : null;

                    exObMsgDbContext.QuarantinedOutboxMessages.Add(new QuarantinedOutboxMessage
                    {
                        Id = Guid.NewGuid(),
                        OriginalOutboxMessageId = exObMsg.Id,
                        ClientOrderId = clientOrderId,
                        Payload = exObMsg.Payload,
                        Reason = exObMsg.RetryReason,
                        FinalRetryCount = exObMsg.RetryCount,
                        QuarantinedAt = DateTimeOffset.UtcNow,
                        ErrorMessage = exObMsg.LastError,
                        CorrelationId = exObMsg.CorrelationId
                    });

                    var outboxStub = new OutboxMessage { Id = exObMsg.Id };
                    exObMsgDbContext.OutboxMessages.Attach(outboxStub);
                    exObMsgDbContext.Entry(outboxStub).Property(x => x.ProcessedAt).IsModified = true;
                    outboxStub.ProcessedAt = DateTimeOffset.UtcNow;

                    await exObMsgDbContext.SaveChangesAsync();

                    context.Logger.LogWarning(
                        $"QuarantiningMessage | CorrelationId: {exObMsg.CorrelationId} | OutboxId: {exObMsg.Id} | Reason: {exObMsg.RetryReason} | RetryCount: {exObMsg.RetryCount}");
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(
                        $"QuarantineWriteFailed | CorrelationId: {exObMsg.CorrelationId} | OutboxId: {exObMsg.Id} | Error: {ex.Message}");
                }
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
                var processedOrders = await _tradingDbContext.Orders
                    .Where(x => clientOrderIds.Contains(x.ClientOrderId) && x.IsProcessed)
                    .Select(x => x.ClientOrderId)
                    .ToListAsync();

                alreadyProcessedOrders = new HashSet<Guid>(processedOrders);
            }

            return alreadyProcessedOrders;
        }

        private async Task<(int successCount, int failureCount, bool circuitOpened)> ProcessOutboxMessagesConcurrentlyAsync(
        List<OutboxMessage> outboxMessages, HashSet<Guid> alreadyProcessedClientOrderIds, ILambdaContext context
            )
        {
            var successCount = 0;
            var failureCount = 0;
            var circuitOpenedFlag = 0;

            using var semaphore = new SemaphoreSlim(MaxDegreeOfParallelism);

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
                try
                {
                    if (Guid.TryParse(outboxMessage.Payload, out var clientOrderId))
                    {
                        if (alreadyProcessedClientOrderIds.Contains(clientOrderId))
                        {
                            context.Logger.LogWarning(
                                $"OrderAlreadyProcessed | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | ClientOrderId: {clientOrderId}");

                            await outboxMessageDbContext.OutboxMessages
                                .Where(x => x.Id == outboxMessage.Id && x.ProcessedAt == null)
                                .ExecuteUpdateAsync(x => x.SetProperty(x => x.ProcessedAt, DateTimeOffset.UtcNow));

                            return ProcessOutboxMessageOutcome.AlreadyProcessed;
                        }

                        context.Logger.LogWarning(
                            $"SendingToQueue | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | ClientOrderId: {clientOrderId}");

                        await _circuitBreaker.ExecuteAsync(async () =>
                        {
                            await NotifySqsCreateOrderQueueAsync(clientOrderId, outboxMessage.CorrelationId);
                        });

                        await outboxMessageDbContext.OutboxMessages
                            .Where(x => x.Id == outboxMessage.Id && x.ProcessedAt == null)
                            .ExecuteUpdateAsync(x => x
                            .SetProperty(x => x.ProcessedAt, DateTimeOffset.UtcNow));

                        context.Logger.LogWarning(
                            $"SentToQueue | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | Queue: CREATE_ORDER_QUEUE.fifo");

                        return ProcessOutboxMessageOutcome.Sent;
                    }
                    else
                    {
                        context.Logger.LogError(
                            $"InvalidPayload | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | Payload: {outboxMessage.Payload}");

                        await outboxMessageDbContext.OutboxMessages
                            .Where(x => x.Id == outboxMessage.Id)
                            .ExecuteUpdateAsync(x => x
                            .SetProperty(x => x.RetryCount, (x) => x.RetryCount + 1)
                            .SetProperty(x => x.RetryReason, OutboxRetryReason.InvalidPayload));

                        return ProcessOutboxMessageOutcome.Failure;
                    }
                }
                catch (BrokenCircuitException)
                {
                    context.Logger.LogWarning(
                        $"CircuitOpen | Stopping batch | CorrelationId: {outboxMessage.CorrelationId} | Remaining messages will retry next cycle");
                    return ProcessOutboxMessageOutcome.CircuitOpen;
                }
                catch (AmazonSQSException sqsException)
                {
                    context.Logger.LogError(
                        $"QueueError | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | Error: {sqsException.Message}");

                    await outboxMessageDbContext.OutboxMessages
                            .Where(x => x.Id == outboxMessage.Id)
                            .ExecuteUpdateAsync(x => x
                            .SetProperty(x => x.RetryCount, (x) => x.RetryCount + 1)
                            .SetProperty(x => x.RetryReason, OutboxRetryReason.SimpleQueueServiceUnavailable)
                            .SetProperty(x => x.LastError, sqsException.Message));

                    return ProcessOutboxMessageOutcome.Failure;
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(
                        $"OutboxProcessingFailed | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | Error: {ex.Message}");

                    await outboxMessageDbContext.OutboxMessages
                        .Where(x => x.Id == outboxMessage.Id)
                        .ExecuteUpdateAsync(x => x
                        .SetProperty(x => x.RetryCount, (x) => x.RetryCount + 1)
                        .SetProperty(x => x.RetryReason, OutboxRetryReason.Unknown));

                    return ProcessOutboxMessageOutcome.Failure;
                }
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

        private async Task AutoRecoverResurrectedMessagesAsync(ILambdaContext context)
        {
            var resurrectCandidates = await _tradingDbContext.QuarantinedOutboxMessages
                .Where(q => !q.IsResurrected
                         && !q.IsDiscarded
                         && q.Reason == OutboxRetryReason.SimpleQueueServiceUnavailable)
                .ToListAsync();

            if (resurrectCandidates.Count == 0)
            {
                context.Logger.LogWarning($"AutoRecoveryPhaseSkipped | no resurrection candidates found.");
                return;
            }

            context.Logger.LogWarning($"AutoRecoveryPhase | Found {resurrectCandidates.Count} resurrection candidates");

            var originalMessageIds = resurrectCandidates
                .Select(c => c.OriginalOutboxMessageId)
                .ToHashSet();

            var existingOriginalMessageIds = await _tradingDbContext.OutboxMessages
                .Where(x => originalMessageIds.Contains(x.Id))
                .Select(x => x.Id)
                .ToHashSetAsync();

            using var semaphore = new SemaphoreSlim(MaxDegreeOfParallelism);

            var tasks = resurrectCandidates.Select(async candidate =>
            {
                if (!existingOriginalMessageIds.Contains(candidate.OriginalOutboxMessageId))
                {
                    await MarkCandidateDiscardedAsync(candidate, context);
                    return;
                }

                await semaphore.WaitAsync();

                try
                {
                    await AutoRecoverResurrectedMessageAsync(candidate, context);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            context.Logger.LogWarning($"AutoRecoveryComplete | Resurrected {resurrectCandidates.Count} messages");
        }

        private async Task AutoRecoverResurrectedMessageAsync(QuarantinedOutboxMessage candidate, ILambdaContext context)
        {
            TradingDbContext candidateDbContext;

            try
            {
                candidateDbContext = await _dbContextFactory.CreateDbContextAsync();
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"DbContextCreationFailed | CorrelationId: {candidate.CorrelationId} | Error: {ex.Message}");
                return;
            }

            await using (candidateDbContext)
            {
                try
                {
                    var outboxStub = new OutboxMessage { Id = candidate.OriginalOutboxMessageId };
                    candidateDbContext.OutboxMessages.Attach(outboxStub);
                    candidateDbContext.Entry(outboxStub).Property(x => x.ProcessedAt).IsModified = true;
                    candidateDbContext.Entry(outboxStub).Property(x => x.RetryCount).IsModified = true;
                    candidateDbContext.Entry(outboxStub).Property(x => x.RetryReason).IsModified = true;
                    outboxStub.ProcessedAt = null;
                    outboxStub.RetryCount = 4;
                    outboxStub.RetryReason = OutboxRetryReason.None;

                    var quarantineStub = new QuarantinedOutboxMessage { Id = candidate.Id };
                    candidateDbContext.QuarantinedOutboxMessages.Attach(quarantineStub);
                    candidateDbContext.Entry(quarantineStub).Property(x => x.IsResurrected).IsModified = true;
                    candidateDbContext.Entry(quarantineStub).Property(x => x.ResurrectedAt).IsModified = true;
                    candidateDbContext.Entry(quarantineStub).Property(x => x.ResolutionNotes).IsModified = true;
                    quarantineStub.IsResurrected = true;
                    quarantineStub.ResurrectedAt = DateTimeOffset.UtcNow;
                    quarantineStub.ResolutionNotes = "Auto-resurrected: Queue connectivity restored";

                    await candidateDbContext.SaveChangesAsync();

                    context.Logger.LogWarning(
                        $"ResurrectingMessage | CorrelationId: {candidate.CorrelationId} | OutboxId: {candidate.OriginalOutboxMessageId} | QuarantinedId: {candidate.Id}");
                }
                catch (DbUpdateConcurrencyException)
                {
                    await MarkCandidateDiscardedAsync(candidate, context);
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(
                        $"AutoRecoveryWriteFailed | CorrelationId: {candidate.CorrelationId} | QuarantinedId: {candidate.Id} | Error: {ex.Message}");
                }
            }
        }

        private async Task MarkCandidateDiscardedAsync(QuarantinedOutboxMessage candidate, ILambdaContext context)
        {
            TradingDbContext candidateDbContext;

            try
            {
                candidateDbContext = await _dbContextFactory.CreateDbContextAsync();
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"DbContextCreationFailed | CorrelationId: {candidate.CorrelationId} | Error: {ex.Message}");
                return;
            }

            await using (candidateDbContext)
            {
                context.Logger.LogWarning(
                    $"CandidateDiscardStarted | CorrelationId: {candidate.CorrelationId} | OutboxId: {candidate.OriginalOutboxMessageId} | QuarantinedId: {candidate.Id}");
                try
                {
                    var quarantineStub = new QuarantinedOutboxMessage { Id = candidate.Id };
                    candidateDbContext.QuarantinedOutboxMessages.Attach(quarantineStub);
                    candidateDbContext.Entry(quarantineStub).Property(x => x.IsDiscarded).IsModified = true;
                    candidateDbContext.Entry(quarantineStub).Property(x => x.DiscardedAt).IsModified = true;
                    candidateDbContext.Entry(quarantineStub).Property(x => x.DiscardedBy).IsModified = true;
                    quarantineStub.IsDiscarded = true;
                    quarantineStub.DiscardedAt = DateTimeOffset.UtcNow;
                    quarantineStub.DiscardedBy = "TradingApp-AWS admin";

                    await candidateDbContext.SaveChangesAsync();

                    context.Logger.LogWarning(
                        $"CandidateDiscarded | CorrelationId: {candidate.CorrelationId} | OutboxId: {candidate.OriginalOutboxMessageId} | QuarantinedId: {candidate.Id}");
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(
                        $"CandidateDiscardFailed | CorrelationId: {candidate.CorrelationId} | QuarantinedId: {candidate.Id} | Error: {ex.Message}");
                }
            }
        }

        private async Task<bool> IsQueueReachableAsync(ILambdaContext context)
        {
            try
            {
                await _sqsClient.GetQueueAttributesAsync(_createOrderQueueUrl, new List<string> { "QueueArn" });

                context.Logger.LogWarning("QueueReachable | CREATE_ORDER_QUEUE.fifo is accessible.");
                return true;
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning($"QueueUnreachable | Cannot connect to queue | Error: {ex.Message}");
                return false;
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
    }
}
