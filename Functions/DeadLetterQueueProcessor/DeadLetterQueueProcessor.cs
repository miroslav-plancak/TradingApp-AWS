using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Business.Repositories;
using TradingApp.Business.Services.Regular;
using TradingApp.Domain;
using TradingApp.Domain.Models.Enums;
using TradingApp.Events.Payloads;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace DeadLetterQueueProcessor
{
    public class DeadLetterQueueProcessor
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IDeadLetterService _deadLetterService;
        private readonly HttpClient _httpClient;
        private readonly string? _teamsWebhookUrl;

        private const string themeColor = "D70000";

        public DeadLetterQueueProcessor()
        {
            var connectionString = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
            var options = new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connectionString)
                .Options;
            _tradingDbContext = new TradingDbContext(options);

            _httpClient = new HttpClient();

            _teamsWebhookUrl = Environment.GetEnvironmentVariable("TEAMS_DLQ_WEBHOOK_URL") 
                ?? throw new InvalidOperationException("TEAMS_DLQ_WEBHOOK_URL environment variable is not set.");

            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

            var deadLetterRepository = new DeadLetterRepository(
                loggerFactory.CreateLogger<DeadLetterRepository>(), _tradingDbContext);

            _deadLetterService = new DeadLetterService(loggerFactory.CreateLogger<DeadLetterService>(), deadLetterRepository);
        }

        public async Task FunctionHandler(SQSEvent evnt, ILambdaContext context)
        {
            foreach (var record in evnt.Records)
            {
                await ProcessDeadLetterMessage(record, context);
            }
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

                var order = await _tradingDbContext.Orders
                    .FirstOrDefaultAsync(x => x.ClientOrderId == payload.ClientOrderId);

                if (order == null)
                {
                    context.Logger.LogError(
                        $"OrderNotFoundInDatabase | CorrelationId: {correlationId} | ClientOrderId: {payload.ClientOrderId}");

                    var createdDL = await _deadLetterService.CreateDeadLetterLogAsync(
                        record.Body,
                        payload.ClientOrderId,
                        "Order not found in the database.",
                        correlationId);

                    await SendTeamsNotification(createdDL, context);

                    return;
                }

                if (order.IsProcessed)
                {
                    context.Logger.LogWarning(
                        $"OrderAlreadyProcessed | CorrelationId: {correlationId} | ClientOrderId: {payload.ClientOrderId} " +
                        $"| Status: {order.Status}");

                    await _deadLetterService.MarkOutboxMessageAsProcessedAsync(payload.ClientOrderId);
                    return;
                }

                context.Logger.LogError(
                    $"OrderFailedAndInDLQ | CorrelationId: {correlationId} | ClientOrderId: {payload.ClientOrderId}");

                order.Status = OrderStatus.REJECTED;
                order.UpdatedAt = DateTimeOffset.UtcNow;
                order.IsProcessed = true;
                await _tradingDbContext.SaveChangesAsync();

                await _deadLetterService.MarkOutboxMessageAsProcessedAsync(payload.ClientOrderId);

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

                var deadLetterEntry = await _deadLetterService.CreateDeadLetterLogAsync(
                    record.Body,
                    payload.ClientOrderId,
                    reason,
                    correlationId);

                await SendTeamsNotification(deadLetterEntry, context);

                context.Logger.LogWarning(
                    $"DeadLetterProcessed | CorrelationId: {correlationId} | ClientOrderId: {payload.ClientOrderId} | Status: REJECTED");
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"DeadLetterProcessingFailed | CorrelationId: {correlationId} | MessageBody: {record.Body} | Error: {ex.Message}");
                throw;
            }
        }

        private async Task SendTeamsNotification(TradingApp.Business.DTOs.DeadLetter.DeadLetterLogResponseDTO response, ILambdaContext context)
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