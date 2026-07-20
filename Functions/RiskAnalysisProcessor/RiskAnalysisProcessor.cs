using Amazon.Lambda.Annotations;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using System.Text.Json;
using TradingApp.Events.Events;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace RiskAnalysisProcessor
{
    public class RiskAnalysisProcessor
    {
        [LambdaFunction]
        public async Task FunctionHandler(SQSEvent evnt, ILambdaContext context)
        {
            foreach (var record in evnt.Records)
            {
                await ProcessRecord(record, context);
            }
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

            context.Logger.LogWarning($"RiskAnalysisProcessor started | CorrelationId: {correlationId}");

            context.Logger.LogWarning(
                $"Analyzing risk for Order with CorrelationId: {correlationId} | ClientOrderId {orderEvent.ClientOrderId}");

            var riskScore = await CalculateRiskScore(orderEvent);

            context.Logger.LogWarning(
                $"Risk analysis complete with CorrelationId {correlationId} | ClientOrderId: {orderEvent.ClientOrderId} | RiskScore: {riskScore}");
        }

        private async Task<double> CalculateRiskScore(OrderStatusEvent orderEvent)
        {
            await Task.Delay(500);
            var random = new Random();
            return random.NextDouble() * 100;
        }
    }
}
