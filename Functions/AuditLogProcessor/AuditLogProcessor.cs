using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TradingApp.Events.Events;

namespace AuditLogProcessor
{
    public class AuditLogProcessor
    {
        private readonly ILogger<AuditLogProcessor> _logger;

        public AuditLogProcessor(ILogger<AuditLogProcessor> logger)
        {
            _logger = logger;
        }

        [Function(nameof(AuditLogProcessor))]
        public async Task Run
        (
            [ServiceBusTrigger(
            "order_events_topic", 
            "audit-log", 
            Connection = "ServiceBusConnection")]
            ServiceBusReceivedMessage message
        )
        {
            var correlationId = message.CorrelationId ?? "CorrelationId";

            _logger.LogWarning("AuditLogProcessor started | CorrelationId: {CorrelationId}",
                        correlationId);

            var orderEvent = JsonSerializer.Deserialize<OrderStatusEvent>(message.Body.ToString());

            if (orderEvent == null)
            {
                _logger.LogWarning("OrderEventNull | CorrelationId: {CorrelationId}", correlationId);
                return;
            }

            _logger.LogWarning("Writing audit log for Order with CorrelationId: {CorrelationId} | ClientOrderId {ClientOrderId}",
                correlationId, orderEvent.ClientOrderId);

            await WriteAuditLog(orderEvent);

            _logger.LogWarning("Audit log written for Order with CorrelationId: {CorrelationId} | ClientOrderId {ClientOrderId}",
                correlationId, orderEvent.ClientOrderId);

        }

        private async Task WriteAuditLog(OrderStatusEvent orderEvent)
        {
            await Task.Delay(1500);
            await Task.CompletedTask;
        }
    }
}
