using Amazon.Lambda.Annotations;
using Amazon.Lambda.Core;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using Polly.CircuitBreaker;
using System.Text.Json;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.QuarantinedOutboxMessage;
using TradingApp.Domain.Models.Enums;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Handler
{
    public class ScheduledOutboxMessageProcessor
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IAmazonSQS _sqsClient;
        private readonly string _createOrderQueueUrl;
        private readonly AsyncCircuitBreakerPolicy _circuitBreaker;
        private static int _queueFailureCount = 0;

        public ScheduledOutboxMessageProcessor(
            TradingDbContext tradingDbContext,
            IAmazonSQS sqsClient,
            AsyncCircuitBreakerPolicy circuitBreaker)
        {
            _tradingDbContext = tradingDbContext;
            _sqsClient = sqsClient;
            _circuitBreaker = circuitBreaker;

            _createOrderQueueUrl = Environment.GetEnvironmentVariable("CREATE_ORDER_QUEUE_URL")
                ?? throw new InvalidOperationException("CREATE_ORDER_QUEUE_URL environment variable is not set.");
        }

        [LambdaFunction]
        public async Task FunctionHandler(ILambdaContext context)
        {
            context.Logger.LogWarning($"ScheduledOutboxMessageProcessor triggered at: {DateTimeOffset.UtcNow}");

            await QuarantineExhaustedMessages(context);

            var isQueueReachable = await IsQueueReachableAsync(context);

            if (isQueueReachable)
            {
                await ProcessPendingMessages(context);
                await AutoRecoverResurrectedMessages(context);
            }
            else
            {
                context.Logger.LogWarning(
                    "QueueDown | Skipping ProcessPendingMessages() and AutoRecoverResurrectedMessages() this cycle.");
            }

            await _tradingDbContext.SaveChangesAsync();
        }

        private async Task QuarantineExhaustedMessages(ILambdaContext context)
        {
            var exhaustedOutboxMessages = await _tradingDbContext.OutboxMessages
                .Where(x => x.ProcessedAt == null && x.RetryCount >= 5)
                .ToListAsync();

            if (exhaustedOutboxMessages.Count == 0) return;

            context.Logger.LogWarning($"QuarantinePhase | Found {exhaustedOutboxMessages.Count} exhausted messages");

            foreach (var exObMsg in exhaustedOutboxMessages)
            {
                Guid? clientOrderId = Guid.TryParse(exObMsg.Payload, out var parsed) ? parsed : null;

                context.Logger.LogWarning(
                    $"QuarantiningMessage | CorrelationId: {exObMsg.CorrelationId} | OutboxId: {exObMsg.Id} | Reason: {exObMsg.RetryReason} | RetryCount: {exObMsg.RetryCount}");

                _tradingDbContext.QuarantinedOutboxMessages.Add(new QuarantinedOutboxMessage
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

                exObMsg.ProcessedAt = DateTimeOffset.UtcNow;
            }
        }

        private async Task ProcessPendingMessages(ILambdaContext context)
        {
            var outboxMessages = await _tradingDbContext.OutboxMessages
                .Where(x => x.ProcessedAt == null && x.RetryCount < 5)
                .OrderBy(x => x.CreatedAt)
                .Take(50)
                .ToListAsync();

            if (outboxMessages.Count == 0) return;

            context.Logger.LogWarning($"ProcessingPhase | Found {outboxMessages.Count} pending messages");

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

            var successCount = 0;
            var failureCount = 0;
            var circuitOpened = false;

            foreach (var outboxMessage in outboxMessages)
            {
                try
                {
                    if (Guid.TryParse(outboxMessage.Payload, out var clientOrderId))
                    {
                        if (alreadyProcessedOrders.Contains(clientOrderId))
                        {
                            context.Logger.LogWarning(
                                $"OrderAlreadyProcessed | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | ClientOrderId: {clientOrderId}");

                            outboxMessage.ProcessedAt = DateTimeOffset.UtcNow;
                            continue;
                        }

                        context.Logger.LogWarning(
                            $"SendingToQueue | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | ClientOrderId: {clientOrderId}");

                        await _circuitBreaker.ExecuteAsync(async () =>
                        {
                            await NotifySqsCreateOrderQueue(clientOrderId, outboxMessage.CorrelationId);
                        });

                        outboxMessage.ProcessedAt = DateTimeOffset.UtcNow;
                        successCount++;

                        context.Logger.LogWarning(
                            $"SentToQueue | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | Queue: CREATE_ORDER_QUEUE.fifo");
                    }
                    else
                    {
                        context.Logger.LogError(
                            $"InvalidPayload | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | Payload: {outboxMessage.Payload}");

                        outboxMessage.RetryCount++;
                        outboxMessage.RetryReason = OutboxRetryReason.InvalidPayload;
                        failureCount++;
                    }
                }
                catch (BrokenCircuitException)
                {
                    circuitOpened = true;

                    context.Logger.LogWarning(
                        $"CircuitOpen | Stopping batch | CorrelationId: {outboxMessage.CorrelationId} | Remaining messages will retry next cycle");
                    break;
                }
                catch (AmazonSQSException sqsException)
                {
                    context.Logger.LogError(
                        $"QueueError | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | Error: {sqsException.Message}");

                    outboxMessage.RetryCount++;
                    outboxMessage.RetryReason = OutboxRetryReason.SimpleQueueServiceUnavailable;
                    outboxMessage.LastError = sqsException.Message;
                    failureCount++;
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(
                        $"OutboxProcessingFailed | CorrelationId: {outboxMessage.CorrelationId} | OutboxId: {outboxMessage.Id} | Error: {ex.Message}");

                    outboxMessage.RetryCount++;
                    outboxMessage.RetryReason = OutboxRetryReason.Unknown;
                    failureCount++;
                }
            }

            GenerateLogBasedOnResults(context, successCount, failureCount, circuitOpened);
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

        private async Task AutoRecoverResurrectedMessages(ILambdaContext context)
        {
            var resurrectCandidates = await _tradingDbContext.QuarantinedOutboxMessages
                .Where(q => !q.IsResurrected
                         && !q.IsDiscarded
                         && q.Reason == OutboxRetryReason.SimpleQueueServiceUnavailable)
                .ToListAsync();

            if (resurrectCandidates.Count == 0) return;

            context.Logger.LogWarning($"AutoRecoveryPhase | Found {resurrectCandidates.Count} resurrection candidates");

            var originalMessageIds = resurrectCandidates
                .Select(c => c.OriginalOutboxMessageId)
                .ToHashSet();

            var originalMessages = await _tradingDbContext.OutboxMessages
                .Where(x => originalMessageIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id);

            foreach (var candidate in resurrectCandidates)
            {
                if (originalMessages.TryGetValue(candidate.OriginalOutboxMessageId, out var originalOutboxMessage))
                {
                    context.Logger.LogWarning(
                        $"ResurrectingMessage | CorrelationId: {candidate.CorrelationId} | OutboxId: {originalOutboxMessage.Id} | QuarantinedId: {candidate.Id}");

                    originalOutboxMessage.ProcessedAt = null;
                    originalOutboxMessage.RetryCount = 4;
                    originalOutboxMessage.RetryReason = OutboxRetryReason.None;

                    candidate.IsResurrected = true;
                    candidate.ResurrectedAt = DateTimeOffset.UtcNow;
                    candidate.ResolutionNotes = "Auto-resurrected: Queue connectivity restored";
                }
            }

            context.Logger.LogWarning($"AutoRecoveryComplete | Resurrected {resurrectCandidates.Count} messages");
        }

        private async Task NotifySqsCreateOrderQueue(Guid clientOrderId, string correlationId)
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

        private void SimulateQueueFailure(bool isQueueDown)
        {
            if (!isQueueDown) return;

            _queueFailureCount++;

            if (_queueFailureCount <= 3)
            {
                throw new AmazonSQSException(
                    $"SIMULATED: Queue connection failed (failure {_queueFailureCount} of 3)");
            }
        }
    }
}
