using System.Collections.Generic;
using System.Linq;
using TradingApp.Business.DTOs.ConversationFullFile;
using TradingApp.Domain.Models.Entities.ConversationFullFile;

namespace TradingApp.Business.Mappers
{
    public static class ConversationFullFileMapper
    {
        public static List<ConversationFullFile> ToEntities(List<CreateConversationFullFileRequestDTO> dtos)
        {
            if (dtos.Count == 0) return [];

            return dtos.Select(x => new ConversationFullFile
            {
                ConversationId = x.ConversationId,
                SourceFile = x.SourceFile,
                Content = x.Content
            }).ToList();
        }

        public static List<CreatedConversationFullFileResponseDTO> ToCreatedConversationFullFileResponseDTOs(IEnumerable<ConversationFullFile> entities)
        {
            if (!entities.Any()) return [];

            return entities.Select(x => new CreatedConversationFullFileResponseDTO
            {
                ConversationId = x.ConversationId,
                SourceFile = x.SourceFile,
                Content = x.Content
            }).ToList();
        }
    }
}
