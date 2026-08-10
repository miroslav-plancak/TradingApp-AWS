using Amazon.Lambda.Annotations;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;
using System.Text.Json;
using TradingApp.Business.DTOs.DeadLetter;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Business.Mappers;
using TradingApp.Domain;
using TradingApp.Domain.Models.Enums;
using TradingApp.Events.Events;
using TradingApp.Events.Payloads;
using TradingApp.Infrastructure;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace DeadLetterQueueProcessor
{
    public class DeadLetterQueueProcessor
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IDeadLetterService _deadLetterService;
        private readonly HttpClient _httpClient;
        private readonly IAsyncPolicy _sqlResiliencePolicy;
        private readonly IAsyncPolicy _messagingResiliencePolicy;
        private readonly IAmazonSimpleNotificationService _snsClient;

        private readonly string _orderEventsTopicArn;
        private readonly string? _teamsWebhookUrl;
        private const string themeColor = "D70000";

        public DeadLetterQueueProcessor(
           TradingDbContext tradingDbContext,
           IDeadLetterService deadLetterService,
           HttpClient httpClient,
           [FromKeyedServices(ResiliencePolicyKey.Sql)] IAsyncPolicy sqlResiliencePolicy,
           [FromKeyedServices(ResiliencePolicyKey.Messaging)] IAsyncPolicy messagingResiliencePolicy,
           IAmazonSimpleNotificationService snsClient)
        {
            _tradingDbContext = tradingDbContext;
            _deadLetterService = deadLetterService;
            _httpClient = httpClient;
            _sqlResiliencePolicy = sqlResiliencePolicy;
            _messagingResiliencePolicy = messagingResiliencePolicy;
            _snsClient = snsClient;

            _orderEventsTopicArn = Environment.GetEnvironmentVariable("ORDER_EVENTS_TOPIC_ARN")
               ?? throw new InvalidOperationException("ORDER_EVENTS_TOPIC_ARN environment variable is not set.");

            _teamsWebhookUrl = Environment.GetEnvironmentVariable("TEAMS_DLQ_WEBHOOK_URL")
                ?? throw new InvalidOperationException("TEAMS_DLQ_WEBHOOK_URL environment variable is not set.");
        }

        [LambdaFunction]
        public async Task<SQSBatchResponse> FunctionHandler(SQSEvent evnt, ILambdaContext context)
        {
            return await SqsBatchHandler.BatchSqsMessages(evnt, context, ProcessDeadLetterMessage);
        }

        private async Task ProcessDeadLetterMessage(SQSEvent.SQSMessage record, ILambdaContext context)
        {
            SQSEvent.MessageAttribute? correlationIdAttribute = null;
            var hasRealCorrelationId = record.MessageAttributes != null
                && record.MessageAttributes.TryGetValue("CorrelationId", out correlationIdAttribute)
                && !string.IsNullOrEmpty(correlationIdAttribute.StringValue);

            var correlationId = hasRealCorrelationId ? correlationIdAttribute!.StringValue : record.MessageId;

            context.Logger.LogWarning(
                $"DeadLetterMessageReceived | CorrelationId: {correlationId} | MessageId: {record.MessageId} " +
                $"| Time: {DateTimeOffset.UtcNow}");

            try
            {
                var payload = JsonSerializer.Deserialize<OrderPayload>(record.Body);

                if (payload == null)
                {
                    context.Logger.LogError(
                        $"DeadLetterDeserializationFailed | CorrelationId: {correlationId} | MessageBody: {record.Body}");
                    return;
                }

                context.Logger.LogWarning(
                    $"ProcessingDeadLetter | CorrelationId: {correlationId} | ClientOrderId: {payload.ClientOrderId}");

                var order = await _sqlResiliencePolicy.ExecuteAsync(async () =>
                    await _tradingDbContext.Orders
                     .FirstOrDefaultAsync(x => x.ClientOrderId == payload.ClientOrderId));

                var deadLetterLogAlreadyExists = await _sqlResiliencePolicy.ExecuteAsync(async () =>
                      await _tradingDbContext.DeadLetterLogs
                        .FirstOrDefaultAsync(x => x.ClientOrderId == payload.ClientOrderId && !x.IsResolved));

                if(deadLetterLogAlreadyExists != null)
                {
                    context.Logger.LogWarning(
                      $"DeadLetterLogEntryAlreadyExists | CorrelationId: {correlationId} | ClientOrderId: {payload.ClientOrderId}");
                    return;
                }

                if (order == null)
                {
                    context.Logger.LogError(
                        $"OrderNotFoundInDatabase | CorrelationId: {correlationId} | ClientOrderId: {payload.ClientOrderId}");

                    var createdDL = await _deadLetterService.CreateDeadLetterLogAsync(
                        record.Body,
                        payload.ClientOrderId,
                        "Order not found in the database.",
                        DeadLetterCategory.InfrastructureFailure,
                        correlationId);

                    await SendTeamsNotification(createdDL, context);

                    return;
                }

                if (order.IsProcessed)
                {
                    context.Logger.LogWarning(
                        $"OrderAlreadyProcessed | CorrelationId: {correlationId} | ClientOrderId: {payload.ClientOrderId} " +
                        $"| Status: {order.Status}");

                    await _sqlResiliencePolicy.ExecuteAsync(async () =>
                        await _deadLetterService.MarkOutboxMessageAsProcessedAsync(payload.ClientOrderId));

                    return;
                }

                context.Logger.LogError(
                    $"OrderFailedAndInDLQ | CorrelationId: {correlationId} | ClientOrderId: {payload.ClientOrderId}");

                // Staged (Add, not saved) before the retried block below - Polly re-runs that whole
                // lambda on a transient failure, and CreateDeadLetterLogAsync's own Add+Save isn't
                // idempotent, so building the entity here means at most one row is ever pending,
                // however many times the save gets retried.
                var deadLetterEntry = StageDeadLetterLogEntity(record, payload, correlationId);

                await _sqlResiliencePolicy.ExecuteAsync(async () =>
                {
                    order.Status = OrderStatus.REJECTED;
                    order.UpdatedAt = DateTimeOffset.UtcNow;
                    order.IsProcessed = true;

                    await _tradingDbContext.SaveChangesAsync();
                });

                await PublishOrderProcessedEvent(payload.ClientOrderId, OrderStatus.REJECTED, correlationId, context);

                await _sqlResiliencePolicy.ExecuteAsync(async () =>
                    await _deadLetterService.MarkOutboxMessageAsProcessedAsync(payload.ClientOrderId));

                await SendTeamsNotification(deadLetterEntry, context);

                context.Logger.LogWarning(
                    $"DeadLetterProcessed | CorrelationId: {correlationId} | ClientOrderId: {payload.ClientOrderId} | Status: REJECTED");
            }
            catch (DbUpdateException)
            {
                context.Logger.LogError(
                    $"DeadLetterProcessingFailed | CorrelationId: {correlationId} | MessageId: {record.MessageId} " +
                    $"| Reason: database write failed | Action: message will be redriven");
                throw;
            }
            catch (BrokenCircuitException)
            {
                context.Logger.LogWarning(
                    $"CircuitOpen | CorrelationId: {correlationId} | MessageId: {record.MessageId} " +
                    $"| Reason: SQL circuit breaker is open | Action: message will be redriven");
                throw;
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"DeadLetterProcessingFailed | CorrelationId: {correlationId} | MessageBody: {record.Body} | Error: {ex.Message}");
                throw;
            }
        }

        private async Task PublishOrderProcessedEvent(Guid clientOrderId, OrderStatus status, string correlationId, ILambdaContext context)
        {
            try
            {
                var eventPayload = new OrderStatusChangedEvent
                {
                    ClientOrderId = clientOrderId,
                    Status = status.ToString(),
                    EventTime = DateTimeOffset.UtcNow,
                    Sequence = 1,
                    CorrelationId = correlationId
                };

                var messageBody = JsonSerializer.Serialize(eventPayload);

                var request = new PublishRequest
                {
                    TopicArn = _orderEventsTopicArn,
                    Message = messageBody,
                    Subject = "OrderPersistedToDLQ",
                    MessageGroupId = clientOrderId.ToString(),
                    MessageDeduplicationId = Guid.NewGuid().ToString()
                };

                context.Logger.LogWarning(
                    $"PublishingEventToTopic | CorrelationId: {correlationId} | ClientOrderId: {clientOrderId} | Topic: order_events_topic.fifo");

                await _messagingResiliencePolicy.ExecuteAsync(async () =>
                {
                    await _snsClient.PublishAsync(request);
                });

                context.Logger.LogWarning(
                    $"EventPublishedToTopic | CorrelationId: {correlationId} | ClientOrderId: {clientOrderId} | Topic: order_events_topic.fifo");
            }
            catch (BrokenCircuitException)
            {
                context.Logger.LogWarning(
                    $"CircuitOpen | CorrelationId: {correlationId} | ClientOrderId: {clientOrderId} | Status: {status} " +
                    $"| Action: deferring publish to UnpublishedTopicMessages");

                await _tradingDbContext.SaveUnpublishedTopicMessagesAsync(clientOrderId, status, correlationId);

                context.Logger.LogWarning(
                    $"SavedToUnpublishedTopicMessages | CorrelationId: {correlationId} | ClientOrderId: {clientOrderId}");

                return;
            }
            catch (AmazonSimpleNotificationServiceException snsException)
            {
                context.Logger.LogError(
                    $"TopicPublishFailed | CorrelationId: {correlationId} | ClientOrderId: {clientOrderId} | Error: {snsException.Message}");

                await _tradingDbContext.SaveUnpublishedTopicMessagesAsync(clientOrderId, status, correlationId);

                context.Logger.LogWarning(
                    $"SavedToUnpublishedTopicMessages | CorrelationId: {correlationId} | ClientOrderId: {clientOrderId}");

                return;
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"EventPublishFailed | CorrelationId: {correlationId} | ClientOrderId: {clientOrderId} | Error: {ex.Message}");

                await _tradingDbContext.SaveUnpublishedTopicMessagesAsync(clientOrderId, status, correlationId);

                context.Logger.LogWarning(
                    $"SavedToUnpublishedTopicMessages | CorrelationId: {correlationId} | ClientOrderId: {clientOrderId}");

                return;
            }
        }

        /// <summary>
        /// Builds the DeadLetterLog entity and stages it (Add, no save) - the caller's own
        /// SaveChangesAsync is what actually commits it, together with the order update.
        /// </summary>
        private DeadLetterLogResponseDTO StageDeadLetterLogEntity(SQSEvent.SQSMessage record, OrderPayload payload, string correlationId)
        {
            // WORKAROUND: Azure's ServiceBusReceivedMessage.DeadLetterReason is populated
            // automatically by Service Bus itself (e.g. "MaxDeliveryCountExceeded"). SQS has no
            // equivalent - a redriven message carries no reason string at all. Closest honest
            // substitute: report the real ApproximateReceiveCount SQS *did* give us (via the
            // harness requesting it as a system attribute), rather than inventing a fake reason.
            var receiveCount = record.Attributes != null
                && record.Attributes.TryGetValue("ApproximateReceiveCount", out var countStr)
                ? countStr
                : "unknown";
            var reason = $"SQS redrive: maxReceiveCount exceeded (ApproximateReceiveCount={receiveCount})";

            var deadLetterEntity = DeadLetterMapper.ToEntity(record.Body, payload.ClientOrderId, reason, DeadLetterCategory.BusinessFailure, correlationId);
            deadLetterEntity.Id = Guid.NewGuid();
            deadLetterEntity.CreatedAt = DateTimeOffset.UtcNow;
            deadLetterEntity.IsResolved = false;

            _tradingDbContext.DeadLetterLogs.Add(deadLetterEntity);

            return DeadLetterMapper.ToDeadLetterLogResponseDTO(deadLetterEntity);
        }

        private async Task SendTeamsNotification(DeadLetterLogResponseDTO response, ILambdaContext context)
        {
            if (string.IsNullOrEmpty(_teamsWebhookUrl))
            {
                context.Logger.LogWarning(
                    $"TeamsNotificationSkipped | TEAMS_DLQ_WEBHOOK_URL not set | ClientOrderId: {response.ClientOrderId}");
                return;
            }

            var payload = new
            {
                type = "MessageCard",
                context = "http://schema.org/extensions",
                themeColor = themeColor,
                summary = $"DeadLetter: {response.ClientOrderId} correlationId: {response.CorrelationId}",
                sections = new[]
                {
                      new
                      {
                          activityTitle = $"Dead Letter received!",
                          activitySubTitle = $"Dead letter entry created - correlationId: **{response.CorrelationId}**",
                          facts = new[]
                          {
                              new { name = "Order ID:",         value = response.ClientOrderId.ToString() },
                              new { name = "Reason:",           value = response.Reason },
                              new { name = "Created At:",       value = response.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss UTC") },
                              new { name = "Is Resolved:",      value = response.IsResolved.ToString() },
                              new { name = "Resolution Notes:", value = response.ResolutionNotes ?? "-" },
                              new { name = "Resolved At:",      value = response.ResolvedAt?.ToString("yyyy-MM-dd HH:mm:ss UTC") ?? "-" },
                              new { name = "Resolved by:",      value = response.ResolvedBy ?? "-"},
                              new { name = "MessageBody:",      value = response.MessageBody.Length > 100
                              ? response.MessageBody[..100] + "..."
                              : response.MessageBody },
                          },
                          markdown = true
                      }
                  }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            context.Logger.LogWarning(
                $"SendingTeamsNotification | ClientOrderId: {response.ClientOrderId} | CorrelationId: {response.CorrelationId}");

            try
            {
                var httpResponse = await _httpClient.PostAsync(_teamsWebhookUrl, content);

                if (httpResponse.IsSuccessStatusCode)
                {
                    context.Logger.LogWarning(
                        $"TeamsNotificationSent | ClientOrderId: {response.ClientOrderId} | CorrelationId: {response.CorrelationId}");
                }
                else
                {
                    var responseBody = await httpResponse.Content.ReadAsStringAsync();
                    context.Logger.LogError(
                        $"TeamsNotificationFailed | ClientOrderId: {response.ClientOrderId} | CorrelationId: {response.CorrelationId} | Response: {responseBody}");
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"TeamsNotificationException | ClientOrderId: {response.ClientOrderId} | CorrelationId: {response.CorrelationId} | Error: {ex.Message}");
            }
        }
    }
}