using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using System.Text.Json;
using TradingApp.Domain;
using TradingApp.Events.Events;

namespace ScheduledUnpublishedTopicMessagesProcessor
{
    public class ScheduledUnpublishedTopicMessagesProcessor
    {
        private readonly ILogger<ScheduledUnpublishedTopicMessagesProcessor> _logger;
        private readonly TradingDbContext _tradingDbContext;
        private readonly ServiceBusClient _serviceBusClient;
        private readonly ServiceBusSender _sender;
        private readonly AsyncCircuitBreakerPolicy _circuitBreaker;
        private static int _topicFailureCount = 0;

        public ScheduledUnpublishedTopicMessagesProcessor
            (
            ILogger<ScheduledUnpublishedTopicMessagesProcessor> logger,
            TradingDbContext tradingDbContext,
            IConfiguration configuration,
            AsyncCircuitBreakerPolicy circuitBreaker
            )
        {
            _logger = logger;
            _tradingDbContext = tradingDbContext;
            var connectionString = configuration["ServiceBusConnectionString"];
            _serviceBusClient = new ServiceBusClient(connectionString);
            _sender = _serviceBusClient.CreateSender("order_events_topic");
            _circuitBreaker = circuitBreaker;
        }

        [Function("ScheduledUnpublishedTopicMessagesProcessor")]
        public async Task Run([TimerTrigger("0 */1 * * * *")] TimerInfo myTimer)
        {
            _logger.LogWarning("ScheduledUnpublishedTopicMessagesProcessor triggered at: {TriggerTime}",
                DateTimeOffset.UtcNow);

            var unpublishedMessages = await _tradingDbContext.UnpublishedTopicMessages
                .Where(x => x.PublishedAt == null && x.RetryCount < 5)
                .OrderBy(x => x.CreatedAt)
                .ThenBy(x => x.OrderStatus)
                .Take(50)
                .ToListAsync();

            if (unpublishedMessages.Count == 0)
            {
                _logger.LogWarning("NoUnpublishedMessages | No messages to retry");
                return;
            }

            _logger.LogWarning("RetryingUnpublishedMessages | Found {Count} messages to retry",
                unpublishedMessages.Count);

            var successCount = 0;
            var failureCount = 0;
            var circuitOpened = false;

            foreach (var unpublishedMessage in unpublishedMessages)
            {
                try
                {
                    _logger.LogWarning(
                        "RetryingTopicPublish | CorrelationId: {CorrelationId} | UnpublishedId: {UnpublishedId} | ClientOrderId: {ClientOrderId}",
                        unpublishedMessage.CorrelationId, unpublishedMessage.Id, unpublishedMessage.ClientOrderId);

                    var eventPayload = new OrderStatusEvent
                    {
                        ClientOrderId = unpublishedMessage.ClientOrderId,
                        Status = unpublishedMessage.OrderStatus.ToString(),
                        EventTime = unpublishedMessage.ProcessedAt,
                        Sequence = 1,
                        CorrelationId = unpublishedMessage.CorrelationId,
                    };

                    var messageBody = JsonSerializer.Serialize(eventPayload);

                    var message = new ServiceBusMessage(messageBody)
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        ContentType = "application/json",
                        Subject = "OrderProcessed",
                        SessionId = unpublishedMessage.ClientOrderId.ToString()
                    };

                    await _circuitBreaker.ExecuteAsync(async () =>
                    {
                        SimulateTopicFailure(false);
                        await _sender.SendMessageAsync(message);
                    });

                    unpublishedMessage.PublishedAt = DateTimeOffset.UtcNow;
                    successCount++;

                    _logger.LogWarning(
                        "TopicPublishRetrySucceeded | CorrelationId: {CorrelationId} | UnpublishedId: {UnpublishedId}",
                        unpublishedMessage.CorrelationId, unpublishedMessage.Id);
                }
                catch (BrokenCircuitException) 
                {
                    circuitOpened = true;

                    _logger.LogWarning(
                       "CircuitOpen | Topic circuit open | Stopping retry batch | CorrelationId: {CorrelationId}",
                       unpublishedMessage.CorrelationId);
                    break;
                }
                catch (ServiceBusException sbEx)
                {
                    unpublishedMessage.RetryCount++;
                    unpublishedMessage.LastError = sbEx.Message;
                    failureCount++;

                    _logger.LogError(sbEx,
                        "TopicPublishRetryFailed | CorrelationId: {CorrelationId} | UnpublishedId: {UnpublishedId} | Error: {Message}",
                        unpublishedMessage.CorrelationId, unpublishedMessage.Id, sbEx.Message);
                }
                catch (Exception ex)
                {
                    unpublishedMessage.RetryCount++;
                    failureCount++;

                    _logger.LogError(ex,
                        "TopicPublishRetryFailed | CorrelationId: {CorrelationId} | UnpublishedId: {UnpublishedId}",
                        unpublishedMessage.CorrelationId, unpublishedMessage.Id);
                }
            }

            await _tradingDbContext.SaveChangesAsync();

            GenerateLogBasedOnResults(successCount, failureCount, circuitOpened);
        }

        private void GenerateLogBasedOnResults(int successCount, int failureCount, bool circuitOpened)
        {
            if (circuitOpened)
            {
                _logger.LogWarning(
                    "RetryBatchAborted | CircuitOpen | Succeeded: {Success} | Failed: {Failed} | Remaining will retry next cycle",
                    successCount, failureCount);
            }
            else if (failureCount > 0 && successCount == 0)
            {
                _logger.LogWarning(
                    "RetryBatchFailed | All messages failed | Failed: {Failed}",
                    failureCount);
            }
            else if (failureCount > 0)
            {
                _logger.LogWarning(
                    "RetryBatchPartial | Succeeded: {Success} | Failed: {Failed}",
                    successCount, failureCount);
            }
            else
            {
                _logger.LogWarning(
                    "RetryProcessingComplete | All messages published | Succeeded: {Success}",
                    successCount);
            }
        }

        private void SimulateTopicFailure(bool isTopicDown)
        {
            if (!isTopicDown) return;

            _topicFailureCount++;

            if (_topicFailureCount <= 3)
            {
                _logger.LogWarning(
                    "SIMULATION | Simulating topic outage | FailureCount: {Count}",
                    _topicFailureCount);

                throw new ServiceBusException(
                    $"SIMULATED: Topic connection failed (failure {_topicFailureCount} of 3)",
                    ServiceBusFailureReason.ServiceCommunicationProblem);
            }
        }
    }
}
