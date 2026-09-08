using System.Collections.Generic;
using System.Linq;
using TradingApp.Business.DTOs.ConversationChunk;
using TradingApp.Domain.Models.Entities.ConversationChunk;

namespace TradingApp.Business.Mappers
{
    public class ConversationChunkMapper
    {
        public static List<ConversationChunk> ToEntities(List<CreateConversationChunkRequestDTO> request)
        {
            if (request == null) return null;

            return request.Select(x => new ConversationChunk
            {
                ConversationId = x.ConversationId,
                Key = x.Key,
                SourceFile = x.SourceFile,
                Content = x.Content
            }).ToList();
        }

        //NOTE: might need later
        //public static CreatedConversationChunkDTO ToCreatedConversationChunkResponseDTO(ConversationChunk entity)
        //{
        //    if (entity == null) return null;

        //    return new CreatedConversationChunkDTO
        //    {
        //        ConversationId = entity.ConversationId,
        //        Key = entity.Key,
        //        SourceFile = entity.SourceFile,
        //        Content = entity.Content
        //    };
        //}
    }
}
