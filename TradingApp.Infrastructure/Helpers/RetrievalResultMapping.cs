using TradingApp.Business.DTOs.ConversationChunk;
using TradingApp.Business.DTOs.ConversationFullFile;
using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Helpers
{
    public static class RetrievalResultMapping
    {
        public static List<RetrievedChunk> ToRetrievedChunks(List<CreatedConversationChunkResponseDTO> chunks)
        {
            return chunks.Select(x => new RetrievedChunk
            {
                Key = x.Key,
                SourceFile = x.SourceFile,
                Content = x.Content,
                KnnScore = null,
                LexicalScore = null,
                RelevanceScore = 0,
                ReciprocalRankFusionScore = 0
            }).ToList();
        }

        public static Dictionary<string, string> ToFullFileContents(List<CreatedConversationFullFileResponseDTO> fullFiles)
        {
            return fullFiles.ToDictionary(x => x.SourceFile, x => x.Content);
        }

        public static List<CreateConversationChunkRequestDTO> ToCreateConversationChunkRequestDTOs(List<RetrievedChunk> chunkFallbacks, Guid conversationId)
        {
            if (chunkFallbacks.Count == 0) return [];

            return chunkFallbacks.Select(x => new CreateConversationChunkRequestDTO
            {
                ConversationId = conversationId,
                Key = x.Key,
                SourceFile = x.SourceFile,
                Content = x.Content
            }).ToList();
        }

        public static List<CreateConversationFullFileRequestDTO> ToCreateConversationFullFileRequestDTOs(Dictionary<string, string> fullFileContents, Guid conversationId)
        {
            if (fullFileContents.Count == 0) return [];

            return fullFileContents.Select(x => new CreateConversationFullFileRequestDTO
            {
                ConversationId = conversationId,
                SourceFile = x.Key,
                Content = x.Value
            }).ToList();
        }
    }
}
