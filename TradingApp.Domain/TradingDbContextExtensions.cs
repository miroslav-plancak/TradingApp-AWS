using Microsoft.EntityFrameworkCore;
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

        public async static Task<UnpublishedTopicMessage?> CheckIfUnpublishedTopicMessageExistsAsync
        (
           this TradingDbContext dbContext,
           Guid clientOrderId,
           OrderStatus status,
           string correlationId
        )
        {
           var result = await dbContext.UnpublishedTopicMessages
                .FirstOrDefaultAsync(x =>
                    x.ClientOrderId == clientOrderId &&
                    x.OrderStatus == status &&
                    (string.IsNullOrEmpty(correlationId) || x.CorrelationId == correlationId));

            return result;
        }
    }
}
