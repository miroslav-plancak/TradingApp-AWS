using System;
using TradingApp.Business.DTOs.ConversationMessage;
using TradingApp.Domain.Models.Entities.ConversationMessage;
using TradingApp.Domain.Models.Enums;

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

        public static CreateConversationMessageRequestDTO ToCreateConversationMessageRequestDTO(Guid conversationId, Guid? clientRequestId, ConversationMessageRole role, string body)
        {
            //TODO: Claude please do the checks for me here before build this object
            return new CreateConversationMessageRequestDTO
            {
                ConversationId = conversationId,
                ClientRequestId = clientRequestId,
                Role = role,
                Body = body
            };
        }
    }
}
