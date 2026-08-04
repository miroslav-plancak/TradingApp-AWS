using Amazon.Lambda.Annotations;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using System.Text.Json;
using TradingApp.Events.Events;
using TradingApp.Infrastructure;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace AuditLogProcessor
{
    public class AuditLogProcessor
    {
        [LambdaFunction]
        public async Task<SQSBatchResponse> FunctionHandler(SQSEvent evnt, ILambdaContext context)
        {
            return await SqsBatchHandler.BatchSqsMessages(evnt, context, ProcessRecord);
        }

        private async Task ProcessRecord(SQSEvent.SQSMessage record, ILambdaContext context)
        {
            var orderEvent = JsonSerializer.Deserialize<OrderStatusEvent>(record.Body);

            if (orderEvent == null)
            {
                context.Logger.LogWarning($"OrderEventNull | MessageId: {record.MessageId}");
                return;
            }

            var correlationId = orderEvent.CorrelationId;

            context.Logger.LogWarning($"AuditLogProcessor started | CorrelationId: {correlationId}");

            context.Logger.LogWarning(
                $"Writing audit log for Order with CorrelationId: {correlationId} | ClientOrderId {orderEvent.ClientOrderId}");

            await WriteAuditLog(orderEvent);

            context.Logger.LogWarning(
                $"Audit log written for Order with CorrelationId: {correlationId} | ClientOrderId {orderEvent.ClientOrderId}");
        }

        private async Task WriteAuditLog(OrderStatusEvent orderEvent)
        {
            await Task.Delay(1500);
            await Task.CompletedTask;
        }
    }
}
