using Amazon.Lambda.Core;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.CircuitBreaker;
using System.Text.Json;
using TradingApp.Domain;
using TradingApp.Domain.Models.Enums;
using TradingApp.Events.Events;

namespace ScheduledUnpublishedTopicMessagesProcessor
{
    public class ScheduledUnpublishedTopicMessagesProcessor
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly string _orderEventsTopicArn;
        private readonly AsyncCircuitBreakerPolicy _circuitBreaker;
        private static int _topicFailureCount = 0;
        private ILambdaContext? _currentContext;

        public ScheduledUnpublishedTopicMessagesProcessor()
        {
            var connectionString = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING")
                ?? throw new InvalidOperationException("SQL_CONNECTION_STRING environment variable is not set.");

            var options = new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connectionString)
                .Options;
            _tradingDbContext = new TradingDbContext(options);

            _snsClient = new AmazonSimpleNotificationServiceClient(Amazon.RegionEndpoint.EUNorth1);

            _orderEventsTopicArn = Environment.GetEnvironmentVariable("ORDER_EVENTS_TOPIC_ARN")
                ?? throw new InvalidOperationException("ORDER_EVENTS_TOPIC_ARN environment variable is not set.");

            _circuitBreaker = Policy
                .Handle<Exception>()
                .CircuitBreakerAsync(
                    exceptionsAllowedBeforeBreaking: 3,
                    durationOfBreak: TimeSpan.FromMinutes(2),
                    onBreak: (exception, duration) =>
                        _currentContext?.Logger.LogWarning(
                            $"CircuitBreaker OPENED | order_events_topic unreachable | Will retry in {duration.TotalSeconds}s | Error: {exception.Message}"),
                    onReset: () =>
                        _currentContext?.Logger.LogWarning("CircuitBreaker CLOSED | Topic connectivity restored"),
                    onHalfOpen: () =>
                        _currentContext?.Logger.LogWarning("CircuitBreaker HALF-OPEN | Testing topic connectivity..."));
        }

        public async Task FunctionHandler(ILambdaContext context)
        {
            _currentContext = context;
            context.Logger.LogWarning($"ScheduledUnpublishedTopicMessagesProcessor triggered at: {DateTimeOffset.UtcNow}");

            var unpublishedMessages = await _tradingDbContext.UnpublishedTopicMessages
                .Where(x => x.PublishedAt == null && x.RetryCount < 5)
                .OrderBy(x => x.CreatedAt)
                .ThenBy(x => x.OrderStatus)
                .Take(50)
                .ToListAsync();

            if (unpublishedMessages.Count == 0)
            {
                context.Logger.LogWarning("NoUnpublishedMessages | No messages to retry");
                return;
            }

            context.Logger.LogWarning($"RetryingUnpublishedMessages | Found {unpublishedMessages.Count} messages to retry");

            var successCount = 0;
            var failureCount = 0;
            var circuitOpened = false;

            foreach (var unpublishedMessage in unpublishedMessages)
            {
                try
                {
                    context.Logger.LogWarning(
                        $"RetryingTopicPublish | CorrelationId: {unpublishedMessage.CorrelationId} " +
                        $"| UnpublishedId: {unpublishedMessage.Id} | ClientOrderId: {unpublishedMessage.ClientOrderId}");

                    var eventPayload = new OrderStatusEvent
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

                    await _circuitBreaker.ExecuteAsync(async () =>
                    {
                        SimulateTopicFailure(false, context);
                        await _snsClient.PublishAsync(request);
                    });

                    unpublishedMessage.PublishedAt = DateTimeOffset.UtcNow;
                    successCount++;

                    context.Logger.LogWarning(
                        $"TopicPublishRetrySucceeded | CorrelationId: {unpublishedMessage.CorrelationId} | UnpublishedId: {unpublishedMessage.Id}");
                }
                catch (BrokenCircuitException)
                {
                    circuitOpened = true;
                    context.Logger.LogWarning(
                        $"CircuitOpen | Topic circuit open | Stopping retry batch | CorrelationId: {unpublishedMessage.CorrelationId}");
                    break;
                }
                catch (AmazonSimpleNotificationServiceException snsEx)
                {
                    unpublishedMessage.RetryCount++;
                    unpublishedMessage.LastError = snsEx.Message;
                    failureCount++;

                    context.Logger.LogError(
                        $"TopicPublishRetryFailed | CorrelationId: {unpublishedMessage.CorrelationId} " +
                        $"| UnpublishedId: {unpublishedMessage.Id} | Error: {snsEx.Message}");
                }
                catch (Exception ex)
                {
                    unpublishedMessage.RetryCount++;
                    failureCount++;

                    context.Logger.LogError(
                        $"TopicPublishRetryFailed | CorrelationId: {unpublishedMessage.CorrelationId} " +
                        $"| UnpublishedId: {unpublishedMessage.Id} | Error: {ex.Message}");
                }
            }

            await _tradingDbContext.SaveChangesAsync();

            GenerateLogBasedOnResults(successCount, failureCount, circuitOpened, context);
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

            _topicFailureCount++;

            if (_topicFailureCount <= 3)
            {
                context.Logger.LogWarning($"SIMULATION | Simulating topic outage | FailureCount: {_topicFailureCount}");

                throw new InternalErrorException(
                    $"SIMULATED: Topic connection failed (failure {_topicFailureCount} of 3)");
            }
        }
    }
}
