using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using System.Text.Json;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.UnpublishedTopicMessages;
using TradingApp.Domain.Models.Enums;
using TradingApp.Events.Events;

namespace ScheduledOrderStatusProcessor
{
    public class ScheduledOrderStatusProcessor
    {
        private readonly ILogger<ScheduledOrderStatusProcessor> _logger;
        private readonly TradingDbContext _tradingDbContext;
        private readonly string _connectionString;
        private readonly ServiceBusClient _serviceBusClient;
        private readonly ServiceBusSender _sender;
        private readonly AsyncCircuitBreakerPolicy _circuitBreaker;
        private static int _topicFailureCount = 0;

        public ScheduledOrderStatusProcessor
        (
            ILogger<ScheduledOrderStatusProcessor> logger,
            TradingDbContext tradingDbContext,
            IConfiguration configuration,
            AsyncCircuitBreakerPolicy circuitBreaker
        )
        {
            _logger = logger;
            _tradingDbContext = tradingDbContext;
            _connectionString = configuration["ServiceBusConnectionString"]!;
            _serviceBusClient = new ServiceBusClient(_connectionString);
            _sender = _serviceBusClient.CreateSender("order_events_topic");
            _circuitBreaker = circuitBreaker;
        }

        [Function("ScheduledOrderStatusProcessor")]
        public async Task Run([TimerTrigger("0 */1 * * * *")] TimerInfo myTimer)
        {
            _logger.LogWarning("ScheduledOrderStatusProcessor triggered at: {TriggerTime}",
                DateTimeOffset.UtcNow);
            
            var orders = await _tradingDbContext.Orders
                .Where(ao => ao.Status == OrderStatus.ACKNOWLEDGED)
                .OrderBy(x => x.CreatedAt)
                .Take(50)
                .ToListAsync();

            if (orders.Count == 0)
            {
                _logger.LogWarning("NoAcknowledgedOrders | No orders to promote to FILLED");
                return;
            }

            _logger.LogWarning("PromotingOrders | Found {Count} ACKNOWLEDGED orders to promote",
                orders.Count);

            var successCount = 0;
            var failureCount = 0;
            var circuitOpened = false;

            foreach (var order in orders)
            {
                try 
                {
                    _logger.LogWarning(
                        "PromotingOrder | CorrelationId: {CorrelationId} | OrderId: {OrderId} " +
                        "| ClientOrderId: {ClientOrderId} | ACKNOWLEDGED => FILLED",
                    order.CorrelationId, order.Id, order.ClientOrderId);

                    var eventPayload = new OrderStatusEvent
                    {
                        ClientOrderId = order.ClientOrderId,
                        Status = OrderStatus.FILLED.ToString(),
                        EventTime = DateTimeOffset.UtcNow,
                        Sequence = 2,
                        CorrelationId = order.CorrelationId
                    };

                    var messageBody = JsonSerializer.Serialize(eventPayload);

                    var message = new ServiceBusMessage(messageBody)
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        ContentType = "application/json",
                        Subject = "OrderStatusFilled",
                        SessionId = order.ClientOrderId.ToString()
                    };

                    await _circuitBreaker.ExecuteAsync(async () => 
                    {
                        SimulateTopicFailure(false);
                        await _sender.SendMessageAsync(message);
                    });

                    order.Status = OrderStatus.FILLED;
                    order.UpdatedAt = DateTimeOffset.UtcNow;
                    successCount++;

                    _logger.LogWarning(
                        "OrderFilledAndPublished | CorrelationId: {CorrelationId} | ClientOrderId: {ClientOrderId}",
                        order.CorrelationId, order.ClientOrderId);
                }
                catch (BrokenCircuitException)
                {
                    circuitOpened = true;
                    _logger.LogWarning(
                        "CircuitOpen | Stopping batch | CorrelationId: {CorrelationId} | Order stays ACKNOWLEDGED",
                        order.CorrelationId);
                    break;
                }
                catch(ServiceBusException sbEx)
                {
                    failureCount++;
                    _logger.LogError(sbEx,
                            "TopicPublishFailed | CorrelationId: {CorrelationId} | Order stays ACKNOWLEDGED",
                         order.CorrelationId);

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

                    _logger.LogWarning(
                        "SavedToUnpublishedTopicMessages | CorrelationId: {CorrelationId} " +
                        "| ClientOrderId: {ClientOrderId} | Status{Status}",
                        order.CorrelationId, order.ClientOrderId, order.Status);
                }
                catch(Exception ex)
                {
                    failureCount++;
                    _logger.LogError(ex,
                    "UnexpectedError | CorrelationId: {CorrelationId} | Order stays ACKNOWLEDGED",
                         order.CorrelationId);
                }
            }

            await _tradingDbContext.SaveChangesAsync();

            GenerateLogBasedOnResults(successCount, failureCount, circuitOpened, orders.Count);
        }

        private void GenerateLogBasedOnResults(int successCount, int failureCount, bool circuitOpened, int totalOrderCount)
        {
            if (circuitOpened && successCount > 0)
            {
                _logger.LogWarning(
                    "PromotionBatchPartiallyAborted | CircuitOpen | " +
                    "Promoted: {Success} orders to FILLED and notified subscribers | " +
                    "Remaining {Failed} orders stay ACKNOWLEDGED and will be retried next cycle",
                    successCount, failureCount);
            }
            else if (circuitOpened)
            {
                _logger.LogWarning(
                    "PromotionBatchAborted | CircuitOpen | " +
                    "No orders promoted | All {Count} orders stay ACKNOWLEDGED",
                    totalOrderCount); 
            }
            else if (failureCount > 0 && successCount == 0)
            {
                _logger.LogWarning(
                    "PromotionBatchFailed | TopicUnreachable | " +
                    "No orders promoted to FILLED | Failed: {Failed}",
                    failureCount);
            }
            else if (failureCount > 0)
            {
                _logger.LogWarning(
                    "PromotionBatchPartial | " +
                    "Promoted: {Success} | Failed to notify: {Failed}",
                    successCount, failureCount);
            }
            else
            {
                _logger.LogWarning(
                    "PromotionBatchComplete | " +
                    "Promoted {Success} orders to FILLED | Subscribers notified",
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
