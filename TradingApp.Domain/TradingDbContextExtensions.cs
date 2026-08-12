using Microsoft.EntityFrameworkCore;
using System;
using System.Text.Json;
using System.Threading.Tasks;
using TradingApp.Domain.Models.Entities.UnpublishedTopicMessages;
using TradingApp.Events.Events;

namespace TradingApp.Domain
{
    public static class TradingDbContextExtensions
    {
        public async static Task<UnpublishedTopicMessage?> CheckIfUnpublishedTopicMessageExistsAsync<TEvent>
        (
           this TradingDbContext dbContext,
           Guid clientOrderId,
           string correlationId
        )
            where TEvent : IntegrationEvent
        {
            var eventType = typeof(TEvent).Name;

            var result = await dbContext.UnpublishedTopicMessages
                 .AsNoTracking()
                 .FirstOrDefaultAsync(x =>
                     x.ClientOrderId == clientOrderId &&
                     x.EventType == eventType &&
                     (string.IsNullOrEmpty(correlationId) || x.CorrelationId == correlationId));

            return result;
        }

        public static Task SaveUnpublishedTopicMessageAsync<TEvent>
        (
            this TradingDbContext dbContext,
            TEvent integrationEvent
        )
            where TEvent : IntegrationEvent
        {
            dbContext.UnpublishedTopicMessages.Add(new UnpublishedTopicMessage
            {
                Id = Guid.NewGuid(),
                ClientOrderId = integrationEvent.ClientOrderId,
                EventType = typeof(TEvent).Name,
                Payload = JsonSerializer.Serialize(integrationEvent),
                CreatedAt = DateTimeOffset.UtcNow,
                CorrelationId = integrationEvent.CorrelationId
            });

            return dbContext.SaveChangesAsync();
        }
    }
}