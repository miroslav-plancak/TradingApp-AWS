using System.Collections.Generic;
using System.Linq;
using TradingApp.Business.DTOs.ConversationMessage;
using TradingApp.Domain.Models.Entities.ConversationMessage;

namespace TradingApp.Business.Mappers
{
    public static class ConversationMessageMapper
    {
        public static CreatedConversationMessageResponseDTO ToCreatedConversationMessageResponseDTO(ConversationMessage entity)
        {
            if (entity == null) return null;

            return new CreatedConversationMessageResponseDTO
            {
                Role = entity.Role,
                Body = entity.Body
            };
        }

        public static CreateConversationMessageRequestDTO ToCreateConversationMessageRequestDTO(CreateConversationMessageRequestDTO request)
        {
            return new CreateConversationMessageRequestDTO
            {
                ConversationId = request.ConversationId,
                ClientRequestId = request.ClientRequestId,
                Role = request.Role,
                Body = request.Body
            };
        }

        public static List<ConversationHistoryMessageDTO> ToConversationHistoryMessageDTOs(IEnumerable<ConversationMessage> conversationMessages)
        {
            if (!conversationMessages.Any()) return [];

            return conversationMessages
                .Select(x => new ConversationHistoryMessageDTO
                {
                    Role = x.Role.ToString().ToLowerInvariant(),
                    Content = x.Body,
                    CreatedAt = x.CreatedAt

                })
                .ToList();
        }

        public static ConversationMessageDTO ToConversationMessageDTO(ConversationMessage entity)
        {
            if (entity == null) return null;

            return new ConversationMessageDTO
            {
                ConversationId = entity.ConversationId,
                Role = entity.Role,
                Body = entity.Body,
                CreatedAt = entity.CreatedAt
            };
        }
    }
}
