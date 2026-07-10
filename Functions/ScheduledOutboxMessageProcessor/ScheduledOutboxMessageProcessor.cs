using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using System.Text.Json;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.QuarantinedOutboxMessage;
using TradingApp.Domain.Models.Enums;

namespace ScheduledOutboxMessageProcessor
{
    public class ScheduledOutboxMessageProcessor
    {
        private readonly ILogger<ScheduledOutboxMessageProcessor> _logger;
        private readonly TradingDbContext _tradingDbContext;
        private readonly ServiceBusClient _client;
        private readonly ServiceBusSender _sender;
        private readonly string _connectionString;
        private readonly AsyncCircuitBreakerPolicy _circuitBreaker;
        private static int _serviceBusFailureCount = 0;

        public ScheduledOutboxMessageProcessor
        (
            ILogger<ScheduledOutboxMessageProcessor> logger,
            TradingDbContext tradingDbContext,
            IConfiguration configuration,                     
            AsyncCircuitBreakerPolicy circuitBreakerPolicy
        )
        {
            _logger = logger;
            _tradingDbContext = tradingDbContext;
            _connectionString = configuration["ServiceBusConnectionString"]!;
            _client = new ServiceBusClient(_connectionString);
            _sender = _client.CreateSender("CREATE_ORDER_QUEUE");
            _circuitBreaker = circuitBreakerPolicy;
        }

        [Function("ScheduledOutboxMessageProcessor")]
        public async Task Run([TimerTrigger("0 */1 * * * *")] TimerInfo myTimer)
        {
            _logger.LogWarning("ScheduledOutboxMessageProcessor triggered at: {TriggerTime}",
                DateTimeOffset.UtcNow);

            await QuarantineExhaustedMessages();

            var isServiceBusReachable =  await IsServiceBusReachableAsync();

            if (isServiceBusReachable)
            {
                await ProcessPendingMessages();
                await AutoRecoverResurrectedMessages();
            }
            else 
            {
                _logger.LogWarning(
                    "ServiceBusDown | Skipping ProcessPendingMessages() and" +
                    " AutoRecoverRessurectedMessages() this cyclye."
                    );
            }

            await _tradingDbContext.SaveChangesAsync();
        }

        private async Task QuarantineExhaustedMessages()
        {
            var exhaustedOutboxMessages = await _tradingDbContext.OutboxMessages
                .Where(x => x.ProcessedAt == null && x.RetryCount >= 5)
                .ToListAsync();

            if (exhaustedOutboxMessages.Count == 0) return;

            _logger.LogWarning("QuarantinePhase | Found {Count} exhausted messages",
                exhaustedOutboxMessages.Count);

            foreach (var exObMsg in exhaustedOutboxMessages)
            {
                Guid? clientOrderId = Guid.TryParse(exObMsg.Payload, out var parsed) ? parsed : null;

                _logger.LogWarning(
                    "QuarantiningMessage | CorrelationId: {CorrelationId} | OutboxId: {OutboxId} | Reason: {Reason} | RetryCount: {Count}",
                    exObMsg.CorrelationId, exObMsg.Id, exObMsg.RetryReason, exObMsg.RetryCount);

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

        private async Task ProcessPendingMessages()
        {
            var outboxMessages = await _tradingDbContext.OutboxMessages
                .Where(x => x.ProcessedAt == null && x.RetryCount < 5)
                .OrderBy(x => x.CreatedAt)
                .Take(50)
                .ToListAsync();

            if (outboxMessages.Count == 0) return;

            _logger.LogWarning("ProcessingPhase | Found {Count} pending messages",
                outboxMessages.Count);

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
                            _logger.LogWarning(
                                "OrderAlreadyProcessed | CorrelationId: {CorrelationId} | OutboxId: {OutboxId} | ClientOrderId: {ClientOrderId}",
                                outboxMessage.CorrelationId, outboxMessage.Id, clientOrderId);

                            outboxMessage.ProcessedAt = DateTimeOffset.UtcNow;
                            continue;
                        }

                        _logger.LogWarning(
                            "SendingToServiceBus | CorrelationId: {CorrelationId} | OutboxId: {OutboxId} | ClientOrderId: {ClientOrderId}",
                            outboxMessage.CorrelationId, outboxMessage.Id, clientOrderId);

                        await _circuitBreaker.ExecuteAsync(async () =>
                        {
                            await NotifyServiceBusCreateOrderQueue(
                                clientOrderId,
                                outboxMessage.CorrelationId);
                        });

                        outboxMessage.ProcessedAt = DateTimeOffset.UtcNow;
                        successCount++;

                        _logger.LogWarning(
                            "SentToServiceBus | CorrelationId: {CorrelationId} | OutboxId: {OutboxId} | Queue: CREATE_ORDER_QUEUE",
                            outboxMessage.CorrelationId, outboxMessage.Id);
                    }
                    else
                    {
                        _logger.LogError(
                           "InvalidPayload | CorrelationId: {CorrelationId} | OutboxId: {OutboxId} | Payload: {Payload}",
                            outboxMessage.CorrelationId, outboxMessage.Id, outboxMessage.Payload);

                        outboxMessage.RetryCount++;
                        outboxMessage.RetryReason = OutboxRetryReason.InvalidPayload;
                        failureCount++;
                    }
                }
                catch (BrokenCircuitException)
                {
                    circuitOpened = true;

                    _logger.LogWarning(
                        "CircuitOpen | Stopping batch | CorrelationId: {CorrelationId} | Remaining messages will retry next cycle",
                        outboxMessage.CorrelationId);
                    break;
                }
                catch (ServiceBusException serviceBusException)
                {
                    _logger.LogError(serviceBusException,
                        "ServiceBusError | CorrelationId: {CorrelationId} | OutboxId: {OutboxId} | Error: {Message}",
                        outboxMessage.CorrelationId, outboxMessage.Id, serviceBusException.Message);

                    outboxMessage.RetryCount++;
                    outboxMessage.RetryReason = OutboxRetryReason.ServiceBusUnavailable;
                    outboxMessage.LastError = serviceBusException.Message;
                    failureCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "OutboxProcessingFailed | CorrelationId: {CorrelationId} | OutboxId: {OutboxId}",
                        outboxMessage.CorrelationId, outboxMessage.Id);

                    outboxMessage.RetryCount++;
                    outboxMessage.RetryReason = OutboxRetryReason.Unknown;
                    failureCount++;
                }
            }

            GenerateLogBasedOnResults(successCount, failureCount, circuitOpened);
        }

        private void GenerateLogBasedOnResults(int successCount, int failureCount, bool circuitOpened)
        {
            if (circuitOpened)
            {
                _logger.LogWarning(
                    "ProcessingBatchAborted | CircuitOpen | Succeeded: {Success} | Failed: {Failed} | Remaining will retry next cycle",
                    successCount, failureCount);
            }
            else if (failureCount > 0 && successCount == 0)
            {
                _logger.LogWarning(
                    "ProcessingBatchFailed | All messages failed | Failed: {Failed}",
                    failureCount);
            }
            else if (failureCount > 0)
            {
                _logger.LogWarning(
                    "ProcessingBatchPartial | Succeeded: {Success} | Failed: {Failed}",
                    successCount, failureCount);
            }
            else
            {
                _logger.LogWarning(
                    "ProcessingBatchComplete | All messages sent | Succeeded: {Success}",
                    successCount);
            }
        }

        private async Task AutoRecoverResurrectedMessages()
        {
            var resurrectCandidates = await _tradingDbContext.QuarantinedOutboxMessages
                .Where(q => !q.IsResurrected
                         && !q.IsDiscarded
                         && q.Reason == OutboxRetryReason.ServiceBusUnavailable)
                .ToListAsync();

            if (resurrectCandidates.Count == 0) return;

            _logger.LogWarning("AutoRecoveryPhase | Found {Count} resurrection candidates",
                resurrectCandidates.Count);

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
                    _logger.LogWarning(
                        "ResurrectingMessage | CorrelationId: {CorrelationId} | OutboxId: {OutboxId} | QuarantinedId: {QuarantinedId}",
                        candidate.CorrelationId, originalOutboxMessage.Id, candidate.Id);

                    originalOutboxMessage.ProcessedAt = null;
                    originalOutboxMessage.RetryCount = 4;
                    originalOutboxMessage.RetryReason = OutboxRetryReason.None;

                    candidate.IsResurrected = true;
                    candidate.ResurrectedAt = DateTimeOffset.UtcNow;
                    candidate.ResolutionNotes = "Auto-resurrected: Service Bus connectivity restored";
                }
            }

            _logger.LogWarning(
                "AutoRecoveryComplete | Resurrected {Count} messages",
                resurrectCandidates.Count);
        }

        private async Task NotifyServiceBusCreateOrderQueue(Guid clientOrderId, string correlationId)
        {
            SimulateServiceBusFailure(false);

            var payload = new { ClientOrderId = clientOrderId };

            var serializedPayload = JsonSerializer.Serialize(payload);

            var message = new ServiceBusMessage(serializedPayload)
            {
                MessageId = Guid.NewGuid().ToString(),
                CorrelationId = correlationId
            };

            await _sender.SendMessageAsync(message);
        }

        private async Task<bool> IsServiceBusReachableAsync()
        {
            try
            {
                var adminClient = new ServiceBusAdministrationClient(_connectionString);
                await adminClient.GetQueueRuntimePropertiesAsync("CREATE_ORDER_QUEUE");

                _logger.LogWarning("ServiceBusReachable | CREATE_ORDER_QUEUE is accessible.");

                return true;
            }
            catch (Exception ex) 
            {
                _logger.LogWarning("ServiceBusUnreachable | Cannot connect to Service Bus | Error: {Error}",
                    ex.Message);
                
                return false;
            }
        }

        private void SimulateServiceBusFailure(bool isQueueDown)
        {
            if (!isQueueDown) return;

            _serviceBusFailureCount++;

            if (_serviceBusFailureCount <= 3)
            {
                _logger.LogWarning(
                    "SIMULATION | Simulating ServiceBus  outage | FailureCount: {Count}",
                    _serviceBusFailureCount);

                throw new ServiceBusException(
                    $"SIMULATED: Service bus connection failed (failure {_serviceBusFailureCount} of 3)",
                    ServiceBusFailureReason.ServiceCommunicationProblem);
            }
        }
    }
}
