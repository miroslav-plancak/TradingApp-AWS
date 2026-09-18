using System.Collections.Generic;
using System.Linq;
using TradingApp.Business.DTOs.Conversation;
using TradingApp.Domain.Models.Entities.Conversation;

namespace TradingApp.Business.Mappers
{
    public static class ConversationMapper
    {
        public static Conversation ToEntity(string userMessage)
        {
            if (userMessage == null) return null;

            return new Conversation
            {
                Name = userMessage
            };
        }

        public static CreatedConversationResponseDTO ToCreatedConversationResponseDTO(Conversation entity)
        {
            if (entity == null) return null;

            return new CreatedConversationResponseDTO
            {
                ConversationId = entity.Id,
                Name = entity.Name,
                CreatedAt = entity.CreatedAt,
                UpdatedAt = entity.UpdatedAt
            };
        }

        public static IEnumerable<CreatedConversationResponseDTO> ToCreatedConversationResponseDTOs(IEnumerable<Conversation> entities)
        {
            if (entities == null) return Enumerable.Empty<CreatedConversationResponseDTO>();

            return entities.Select(ToCreatedConversationResponseDTO);
        }

        public static ConversationCompactionStateDTO ToConversationCompactionStateDTO(Conversation entity)
        {
            if (entity == null) return null;

            return new ConversationCompactionStateDTO
            {
                ConversationId = entity.Id,
                CompactedSummary = entity.CompactedSummary,
                SummaryCoversMessagesUpTo = entity.SummaryCoversMessagesUpTo
            };
        }
    }
}
