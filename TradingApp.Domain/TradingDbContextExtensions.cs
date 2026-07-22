using System;
using System.Threading.Tasks;
using TradingApp.Domain.Models.Entities.UnpublishedTopicMessages;
using TradingApp.Domain.Models.Enums;

namespace TradingApp.Domain
{
    public static class TradingDbContextExtensions
    {
        public static Task SaveUnpublishedTopicMessagesAsync
            (
                this TradingDbContext dbContext,
                Guid clientOrderId,
                OrderStatus status,
                string correlationId
            )
        {
            dbContext.UnpublishedTopicMessages.Add(new UnpublishedTopicMessage
            {
                Id = Guid.NewGuid(),
                ClientOrderId = clientOrderId,
                OrderStatus = status,
                ProcessedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                CorrelationId = correlationId
            });

            return dbContext.SaveChangesAsync();
        }
    }
}
