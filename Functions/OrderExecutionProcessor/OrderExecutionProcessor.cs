using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.Order;
using TradingApp.Domain.Models.Enums;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace OrderExecutionProcessor
{
    public class OrderExecutionProcessor
    {
        private readonly TradingDbContext _tradingDbContext;

        public OrderExecutionProcessor()
        {
            var connectionString = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
            var options = new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connectionString)
                .Options;
            _tradingDbContext = new TradingDbContext(options);
        }

        public async Task FunctionHandler(SQSEvent evnt, ILambdaContext context)
        {
            foreach (var record in evnt.Records)
            {
                await ProcessOrderMessage(record, context);
            }
        }

        private async Task ProcessOrderMessage(SQSEvent.SQSMessage record, ILambdaContext context)
        {
            var correlationId = record.MessageId;

            context.Logger.LogWarning(
                $"OrderExecutionStarted | CorrelationId: {correlationId} | MessageId: {record.MessageId}");

            var payload = JsonSerializer.Deserialize<OrderPayload>(record.Body);

            if (payload == null)
            {
                context.Logger.LogError(
                    $"InvalidPayload | CorrelationId: {correlationId} | MessageId: {record.MessageId}");
                return;
            }

            // TODO: unbounded DB calls are even more dangerous here than on Azure - Lambda's configured
            // timeout is 15 SECONDS by default (not the up-to-15-minute Consumption plan window this
            // comment used to refer to), so a hung SQL call kills the whole invocation much faster.
            // Add a command timeout once the connection string / DbContext setup is finalized.
            var orderExists = await _tradingDbContext.Orders
                .AnyAsync(o => o.ClientOrderId == payload.ClientOrderId);

            if (!orderExists)
            {
                context.Logger.LogWarning(
                    $"OrderNotFound | CorrelationId: {correlationId} | ClientOrderId: {payload.ClientOrderId}");
                return;
            }

            var random = new Random();
            var randomStatus = random.Next(2) == 0 ? OrderStatus.ACKNOWLEDGED : OrderStatus.REJECTED;

            var orderRowsProcessed = await _tradingDbContext.Orders
                .Where(x => x.ClientOrderId == payload.ClientOrderId && !x.IsProcessed)
                .ExecuteUpdateAsync(x => x
                    .SetProperty(x => x.Status, randomStatus)
                    .SetProperty(x => x.IsProcessed, true)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow));

            if (orderRowsProcessed == 0)
            {
                context.Logger.LogWarning(
                    $"OrderAlreadyProcessed | CorrelationId: {correlationId} | ClientOrderId: {payload.ClientOrderId}");
                return;
            }

            context.Logger.LogWarning(
                $"OrderProcessed | CorrelationId: {correlationId} | ClientOrderId: {payload.ClientOrderId} | Status: {randomStatus}");

            // TODO: publish to order_events_topic (SNS) once that topic + a publisher exist.
            // Old Azure logic sent a ServiceBusMessage here with SessionId = clientOrderId;
            // the SNS equivalent will need MessageGroupId too if that topic ends up FIFO.
        }
    }
}