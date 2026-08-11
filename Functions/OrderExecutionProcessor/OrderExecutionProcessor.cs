using Amazon.Lambda.Annotations;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using System.Text.Json;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.Order;
using TradingApp.Domain.Models.Enums;
using TradingApp.Events.Events;
using TradingApp.Infrastructure;
using TradingApp.Infrastructure.Interfaces;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace OrderExecutionProcessor
{
    public class OrderExecutionProcessor
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IIntegrationEventPublisher _integrationEventPublisher;
        private readonly IAsyncPolicy _sqlResiliencePolicy;

        // Safe unguarded: FunctionHandler walks evnt.Records with a sequential foreach + await
        // (no Task.WhenAll/Select fan-out in this file), and one execution environment processes
        // one invocation at a time - no two threads ever reach this line concurrently.
        private static int _topicFailureCount = 0;

        public OrderExecutionProcessor(
            TradingDbContext tradingDbContext,
            IIntegrationEventPublisher integrationEventPublisher,
            [FromKeyedServices(ResiliencePolicyKey.Sql)] IAsyncPolicy sqlResiliencePolicy
            )
        {
            _tradingDbContext = tradingDbContext;
            _integrationEventPublisher = integrationEventPublisher;
            _sqlResiliencePolicy = sqlResiliencePolicy;
        }

        [LambdaFunction]
        public async Task<SQSBatchResponse> FunctionHandler(SQSEvent evnt, ILambdaContext context)
        {
            return await SqsBatchHandler.BatchSqsMessages(evnt, context, ProcessOrderMessage);
        }

        private async Task ProcessOrderMessage(SQSEvent.SQSMessage record, ILambdaContext context)
        {
            SimulateRedirectToDeadLetterQueue(false);

            SQSEvent.MessageAttribute? correlationIdAttribute = null;
            var hasRealCorrelationId = record.MessageAttributes != null
                && record.MessageAttributes.TryGetValue("CorrelationId", out correlationIdAttribute)
                && !string.IsNullOrEmpty(correlationIdAttribute.StringValue);

            var correlationId = hasRealCorrelationId ? correlationIdAttribute!.StringValue : record.MessageId;

            context.Logger.LogWarning(
                $"OrderExecutionStarted with {(hasRealCorrelationId ? "real" : "substitute")} " +
                $"correlationId | CorrelationId: {correlationId} | MessageId: {record.MessageId}");

            var payload = JsonSerializer.Deserialize<OrderPayload>(record.Body);

            if (payload == null)
            {
                context.Logger.LogError(
                    $"InvalidPayload | CorrelationId: {correlationId} | MessageId: {record.MessageId}");
                return;
            }

            var orderExists = await _sqlResiliencePolicy.ExecuteAsync(async () =>
                await _tradingDbContext.Orders.AnyAsync(o => o.ClientOrderId == payload.ClientOrderId));

            if (!orderExists)
            {
                context.Logger.LogWarning(
                    $"OrderNotFound | CorrelationId: {correlationId} | ClientOrderId: {payload.ClientOrderId}");
                return;
            }

            var random = new Random();
            var randomStatus = random.Next(2) == 0 ? OrderStatus.ACKNOWLEDGED : OrderStatus.REJECTED; 

            var orderRowsProcessed = await _sqlResiliencePolicy.ExecuteAsync(async () =>
                await _tradingDbContext.Orders
                    .Where(x => x.ClientOrderId == payload.ClientOrderId && !x.IsProcessed)
                    .ExecuteUpdateAsync(x => x
                        .SetProperty(x => x.Status, randomStatus)
                        .SetProperty(x => x.IsProcessed, true)
                        .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow)));

            if (orderRowsProcessed == 0)
            {
                context.Logger.LogWarning(
                    $"OrderAlreadyProcessed | CorrelationId: {correlationId} | ClientOrderId: {payload.ClientOrderId}");
                return;
            }

            context.Logger.LogWarning(
                $"OrderProcessed | CorrelationId: {correlationId} | ClientOrderId: {payload.ClientOrderId} | Status: {randomStatus}");

            var eventPayload = new OrderStatusChangedEvent
            {
                ClientOrderId = payload.ClientOrderId,
                Status = randomStatus.ToString(),
                EventTime = DateTimeOffset.UtcNow,
                Sequence = 1,
                CorrelationId = correlationId
            };
           
            await _integrationEventPublisher.PublishToTopicAsync(
                eventPayload, "OrderProcessed", context,
                simulateTopicFailure: () => SimulateTopicFailure(false, context));
        }

        private void SimulateTopicFailure(bool isTopicDown, ILambdaContext context)
        {
            if (!isTopicDown) return;

            _topicFailureCount++;

            if (_topicFailureCount <= 5)
            {
                context.Logger.LogWarning(
                    $"SIMULATION | Simulating topic outage | FailureCount: {_topicFailureCount}");

                throw new InternalErrorException(
                    $"SIMULATED: Topic connection failed (failure {_topicFailureCount} of 5)")
                {
                    StatusCode = System.Net.HttpStatusCode.ServiceUnavailable
                };
            }
        }

        private void SimulateRedirectToDeadLetterQueue(bool active) 
        {
            if (!active) return;

            throw new Exception("SIMULATED: Message Redirected to DLQ.");
        }
    }
}