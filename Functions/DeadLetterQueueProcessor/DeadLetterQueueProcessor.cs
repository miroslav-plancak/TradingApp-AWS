using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TradingApp.Business.DTOs.DeadLetter;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Domain;
using TradingApp.Domain.Models.Enums;
using TradingApp.Events.Payloads;

namespace DeadLetterQueueProcessor
{
    public class DeadLetterQueueProcessor
    {
        private readonly ILogger<DeadLetterQueueProcessor> _logger;
        private readonly TradingDbContext _tradingDbContext;
        private readonly IDeadLetterService _deadLetterService;
        private readonly HttpClient _httpClient;
        private readonly string _teamsWebhookUrl;

        private const string themeColor = "D70000";
        public DeadLetterQueueProcessor
        (
            ILogger<DeadLetterQueueProcessor> logger,
            TradingDbContext tradingDbContext,
            IDeadLetterService deadLetterService,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory
        )
        {
            _logger = logger;
            _tradingDbContext = tradingDbContext;
            _deadLetterService = deadLetterService;
            _httpClient = httpClientFactory.CreateClient();
            _teamsWebhookUrl = configuration["TeamsDLQWebhookUrl"]!;
        }

        [Function("DeadLetterQueueProcessor")]
        public async Task Run
        (
            [ServiceBusTrigger(
                queueName: "CREATE_ORDER_QUEUE/$DeadLetterQueue",
                Connection = "ServiceBusConnection")]
            ServiceBusReceivedMessage message
        )
        {
            var correlationId = message.CorrelationId ?? "UNKNOWN";
          
            _logger.LogWarning(
                "DeadLetterMessageReceived | CorrelationId: {CorrelationId} | MessageId: {MessageId} | Time: {Time}",
                correlationId, message.MessageId, DateTimeOffset.UtcNow);

            try
            {
                var payload = JsonSerializer.Deserialize<OrderPayload>(message.Body.ToString());

                if (payload == null)
                {
                    _logger.LogError(
                        "DeadLetterDeserializationFailed | CorrelationId: {CorrelationId} | MessageBody: {MessageBody}",
                        correlationId, message.Body.ToString());
                    return;
                }

                _logger.LogWarning(
                    "ProcessingDeadLetter | CorrelationId: {CorrelationId} | ClientOrderId: {ClientOrderId}",
                    correlationId, payload.ClientOrderId);

                var order = await _tradingDbContext.Orders
                    .FirstOrDefaultAsync(x => x.ClientOrderId == payload.ClientOrderId);

                if (order == null)
                {
                    _logger.LogError(
                        "OrderNotFoundInDatabase | CorrelationId: {CorrelationId} | ClientOrderId: {ClientOrderId}",
                        correlationId, payload.ClientOrderId);

                    var createdDL = await _deadLetterService.CreateDeadLetterLogAsync(
                        message.Body.ToString(),
                        payload.ClientOrderId,
                        "Order not found in the database.",
                        correlationId);
                 
                    await SendTeamsNotification(createdDL); 

                    return;
                }

                if (order.IsProcessed)
                {
                    _logger.LogWarning(
                        "OrderAlreadyProcessed | CorrelationId: {CorrelationId} | ClientOrderId: {ClientOrderId} | Status: {Status}",
                        correlationId, payload.ClientOrderId, order.Status);

                    await _deadLetterService.MarkOutboxMessageAsProcessedAsync(payload.ClientOrderId);
                    return;
                }

                _logger.LogError(
                    "OrderFailedAndInDLQ | CorrelationId: {CorrelationId} | ClientOrderId: {ClientOrderId}",
                    correlationId, payload.ClientOrderId);

                order.Status = OrderStatus.REJECTED;
                order.UpdatedAt = DateTimeOffset.UtcNow;
                order.IsProcessed = true;
                await _tradingDbContext.SaveChangesAsync();

                await _deadLetterService.MarkOutboxMessageAsProcessedAsync(payload.ClientOrderId);

                var deadLetterEntry = await _deadLetterService.CreateDeadLetterLogAsync(
                    message.Body.ToString(),
                    payload.ClientOrderId,
                    message.DeadLetterReason, 
                    correlationId);

                await SendTeamsNotification(deadLetterEntry);

                _logger.LogWarning(
                    "DeadLetterProcessed | CorrelationId: {CorrelationId} | ClientOrderId: {ClientOrderId} | Status: REJECTED",
                    correlationId, payload.ClientOrderId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "DeadLetterProcessingFailed | CorrelationId: {CorrelationId} | MessageBody: {MessageBody}",
                    correlationId, message.Body.ToString());

                throw;
            }
        }

        private async Task SendTeamsNotification(DeadLetterLogResponseDTO response)
        {
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

            _logger.LogWarning(
                  "SendingTeamsNotification | ClientOrderId: {ClientOrderId} | CorrelationId: {CorrelationId}",
                  response.ClientOrderId, response.CorrelationId);

            try
            {
                var httpResponse = await _httpClient.PostAsync(_teamsWebhookUrl, content);

                if (httpResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "TeamsNotificationSent | ClientOrderId: {ClientOrderId}  | CorrelationId: {CorrelationId}",
                        response.ClientOrderId, response.CorrelationId);
                }
                else
                {
                    var responseBody = await httpResponse.Content.ReadAsStringAsync();
                    _logger.LogError(
                        "TeamsNotificationFailed | ClientOrderId: {ClientOrderId} | CorrelationId: {CorrelationId} | Response: {Response}",
                        response.ClientOrderId, response.CorrelationId, responseBody);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "TeamsNotificationException | ClientOrderId: {ClientOrderId} | CorrelationId: {CorrelationId}",
                    response.ClientOrderId, response.CorrelationId);
            }
        }
    }
}
