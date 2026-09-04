using Microsoft.AspNetCore.Http.HttpResults;
using System;
using TradingApp.Business.DTOs.Conversation;
using TradingApp.Domain.Models.Entities.Conversation;

namespace TradingApp.Business.Mappers
{
    public static class ConversationMapper
    {
        public static Conversation ToEntity(string userQuery)
        {
            if (userQuery == null) return null;

            return new Conversation
            {
                Name = userQuery
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
    }
}
