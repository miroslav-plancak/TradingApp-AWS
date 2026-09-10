using TradingApp.Business.DTOs.ConversationChunk;

namespace TradingApp.Infrastructure.Interfaces.ConversationMemory
{
    public interface IConversationChunkArbiterService
    {
        Task<List<CreatedConversationChunkResponseDTO>> DetermineSufficientChunksAsync(
            string userQuery, List<CreatedConversationChunkResponseDTO> existingChunks);
    }
}
