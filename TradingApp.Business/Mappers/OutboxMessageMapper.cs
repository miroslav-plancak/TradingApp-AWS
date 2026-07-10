using System.Collections.Generic;
using System.Linq;
using TradingApp.Business.DTOs.Outbox;
using TradingApp.Domain.Models.Entities.OutboxMessage;

namespace TradingApp.Business.Mappers
{
    public static class OutboxMessageMapper
    {
        public static OutboxMessageResponseDTO ToOutboxMessageResponseDTO(OutboxMessage entity)
        {
            if (entity == null) return null;

            return new OutboxMessageResponseDTO
            {
                Id = entity.Id,
                Type = entity.Type,
                Payload = entity.Payload,
                CreatedAt = entity.CreatedAt,
                ProcessedAt = entity.ProcessedAt,
                RetryCount = entity.RetryCount,
                IsProcessed = entity.ProcessedAt.HasValue
            };
        }

        public static IEnumerable<OutboxMessageResponseDTO> ToOutboxMessageResponseDTOs(IEnumerable<OutboxMessage> entities)
        {
            if (entities == null) return Enumerable.Empty<OutboxMessageResponseDTO>();

            return entities.Select(ToOutboxMessageResponseDTO).ToList();
        }
    }
}
