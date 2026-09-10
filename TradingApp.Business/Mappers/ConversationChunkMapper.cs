using System.Collections.Generic;
using System.Linq;
using TradingApp.Business.DTOs.ConversationChunk;
using TradingApp.Domain.Models.Entities.ConversationChunk;

namespace TradingApp.Business.Mappers
{
    public class ConversationChunkMapper
    {
        public static List<ConversationChunk> ToEntities(List<CreateConversationChunkRequestDTO> dtos)
        {
            if (dtos.Count == 0) return [];

            return dtos.Select(x => new ConversationChunk
            {
                ConversationId = x.ConversationId,
                Key = x.Key,
                SourceFile = x.SourceFile,
                Content = x.Content
            }).ToList();
        }

        public static List<CreatedConversationChunkResponseDTO> ToCreatedConversationChunkResultDTOs(IEnumerable<ConversationChunk> entities)
        {
            if (!entities.Any()) return [];

            return entities.Select(x => new CreatedConversationChunkResponseDTO
            {
                ConversationId = x.ConversationId,
                Key = x.Key,
                SourceFile = x.SourceFile,
                Content = x.Content
            }).ToList();
        }
    }
}
