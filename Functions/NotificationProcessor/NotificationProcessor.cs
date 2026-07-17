using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.OrderNotificationSequences;
using TradingApp.Domain.Models.Entities.PendingFilledNotification;
using TradingApp.Events.Events;

namespace NotificationProcessor
{
    public class NotificationProcessor
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly HttpClient _httpClient;
        private readonly string _teamsWebhookUrl;

        public NotificationProcessor()
        {
            var connectionString = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING")
                ?? throw new InvalidOperationException("SQL_CONNECTION_STRING environment variable is not set.");

            var options = new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connectionString)
                .Options;
            _tradingDbContext = new TradingDbContext(options);

            _httpClient = new HttpClient();

            _teamsWebhookUrl = Environment.GetEnvironmentVariable("TEAMS_NOTIFICATION_WEBHOOK_URL")
                ?? throw new InvalidOperationException("TEAMS_NOTIFICATION_WEBHOOK_URL environment variable is not set.");
        }

        public async Task FunctionHandler(SQSEvent evnt, ILambdaContext context)
        {
            foreach (var record in evnt.Records)
            {
                await ProcessRecord(record, context);
            }
        }

        private async Task ProcessRecord(SQSEvent.SQSMessage record, ILambdaContext context)
        {
            var orderStatusEvent = JsonSerializer.Deserialize<OrderStatusEvent>(record.Body);

            if (orderStatusEvent == null)
            {
                context.Logger.LogWarning($"OrderEventNull | MessageId: {record.MessageId}");
                return;
            }

            var correlationId = orderStatusEvent.CorrelationId;
            var messageGroupId = record.Attributes != null && record.Attributes.TryGetValue("MessageGroupId", out var mgid)
                ? mgid
                : "UNKNOWN";

            context.Logger.LogWarning(
                $"NotificationProcessor started | CorrelationId: {correlationId} | MessageGroupId: {messageGroupId}");

            context.Logger.LogWarning(
                $"ReceivedEvent | CorrelationId: {correlationId} | Status: {orderStatusEvent.Status} | Sequence: {orderStatusEvent.Sequence}");

            var tracking = await _tradingDbContext.OrderNotificationSequences
                .FirstOrDefaultAsync(x => x.ClientOrderId == orderStatusEvent.ClientOrderId);

            var lastProcessedSequence = tracking?.LastProcessedSequence ?? 0;

            if (orderStatusEvent.Sequence > lastProcessedSequence + 1)
            {
                context.Logger.LogWarning(
                    $"OutOfOrder | Expected sequence {lastProcessedSequence + 1} but got {orderStatusEvent.Sequence} | " +
                    $"PersistingFilledToDB | CorrelationId: {correlationId}");

                var serializedOrderEventPayload = JsonSerializer.Serialize(orderStatusEvent);

                _tradingDbContext.PendingFilledNotifications.Add(new PendingFilledNotification
                {
                    ClientOrderId = orderStatusEvent.ClientOrderId,
                    EventPayload = serializedOrderEventPayload,
                    CorrelationId = correlationId,
                    StoredAt = DateTimeOffset.UtcNow
                });

                await _tradingDbContext.SaveChangesAsync();

                context.Logger.LogWarning(
                    $"FilledPersistedToDB | ClientOrderId: {orderStatusEvent.ClientOrderId} | CorrelationId: {correlationId}");

                return;
            }

            await ProcessNotification(orderStatusEvent, correlationId, context);

            var pendingFilledOrder = await _tradingDbContext.PendingFilledNotifications
                .FirstOrDefaultAsync(x => x.ClientOrderId == orderStatusEvent.ClientOrderId);

            if (pendingFilledOrder != null)
            {
                context.Logger.LogWarning(
                    $"PendingFilledFound | Sending deferred FILLED | CorrelationId: {correlationId}");

                var deserializedPendingFilledOrder =
                    JsonSerializer.Deserialize<OrderStatusEvent>(pendingFilledOrder.EventPayload);

                if (deserializedPendingFilledOrder != null)
                {
                    await ProcessNotification(deserializedPendingFilledOrder, correlationId, context);
                }

                _tradingDbContext.PendingFilledNotifications.Remove(pendingFilledOrder);

                if (tracking != null)
                {
                    _tradingDbContext.OrderNotificationSequences.Remove(tracking);
                }

                await _tradingDbContext.SaveChangesAsync();

                return;
            }

            if (tracking == null && orderStatusEvent.Status != "REJECTED")
            {
                _tradingDbContext.OrderNotificationSequences.Add(new OrderNotificationSequence
                {
                    ClientOrderId = orderStatusEvent.ClientOrderId,
                    LastProcessedSequence = orderStatusEvent.Sequence,
                    UpdatedAt = DateTimeOffset.UtcNow
                });

                context.Logger.LogWarning(
                    $"TrackingCreated | Sequence: {orderStatusEvent.Sequence} | CorrelationId: {correlationId}");
            }
            else if (tracking != null)
            {
                tracking.LastProcessedSequence = orderStatusEvent.Sequence;
                tracking.UpdatedAt = DateTimeOffset.UtcNow;

                if (orderStatusEvent.Sequence == 2)
                {
                    _tradingDbContext.OrderNotificationSequences.Remove(tracking);

                    context.Logger.LogWarning(
                        $"TrackingRemoved | FinalSequenceProcessed | CorrelationId: {correlationId}");
                }
            }

            await _tradingDbContext.SaveChangesAsync();
        }

        private async Task ProcessNotification(OrderStatusEvent orderEvent, string correlationId, ILambdaContext context)
        {
            context.Logger.LogWarning(
                $"Sending notification for Order with CorrelationId: {correlationId} | ClientOrderId {orderEvent.ClientOrderId} | OrderStatus: {orderEvent.Status}");

            await SendTeamsNotification(orderEvent, correlationId, context);

            context.Logger.LogWarning(
                $"Notification sent for Order with CorrelationId: {correlationId} | ClientOrderId {orderEvent.ClientOrderId} | OrderStatus: {orderEvent.Status}");
        }

        private async Task SendTeamsNotification(OrderStatusEvent orderEvent, string correlationId, ILambdaContext context)
        {
            var statusColor = orderEvent.Status switch
            {
                "ACKNOWLEDGED" => "0076D7",
                "FILLED" => "00B050",
                "REJECTED" => "D70000",
                _ => "808080"
            };

            var statusEmoji = orderEvent.Status switch
            {
                "ACKNOWLEDGED" => "🔵",
                "FILLED" => "🟢",
                "REJECTED" => "🔴",
                _ => "⚪"
            };

            var payload = new
            {
                type = "MessageCard",
                context = "http://schema.org/extensions",
                themeColor = statusColor,
                summary = $"Order: {orderEvent.ClientOrderId} status update: {orderEvent.Status}",
                sections = new[]
                {
                    new
                    {
                        activityTitle = $"{statusEmoji} Order Status Update",
                        activitySubTitle = $"Status changed to **{orderEvent.Status}**",
                        facts = new[]
                        {
                            new { name = "Order ID:", value = orderEvent.ClientOrderId.ToString() },
                            new { name = "Status:", value = orderEvent.Status },
                            new { name = "Processed At:", value = orderEvent.EventTime.ToString("yyyy-MM-dd HH:mm:ss UTC") },
                            new { name = "Correlation ID:", value = correlationId }
                        },
                        markdown = true
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            context.Logger.LogWarning(
                $"SendingTeamsNotification | ClientOrderId: {orderEvent.ClientOrderId} | Status: {orderEvent.Status} | CorrelationId: {correlationId}");

            try
            {
                var httpResponse = await _httpClient.PostAsync(_teamsWebhookUrl, content);

                if (httpResponse.IsSuccessStatusCode)
                {
                    context.Logger.LogWarning(
                        $"TeamsNotificationSent | ClientOrderId: {orderEvent.ClientOrderId} | Status: {orderEvent.Status} | CorrelationId: {correlationId}");
                }
                else
                {
                    var responseBody = await httpResponse.Content.ReadAsStringAsync();
                    context.Logger.LogError(
                        $"TeamsNotificationFailed | ClientOrderId: {orderEvent.ClientOrderId} | Status: {orderEvent.Status} | CorrelationId: {correlationId} | StatusCode: {(int)httpResponse.StatusCode} | Response: {responseBody}");
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"TeamsNotificationException | ClientOrderId: {orderEvent.ClientOrderId} | CorrelationId: {correlationId} | Error: {ex.Message}");
            }
        }
    }
}
