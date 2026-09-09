using TradingApp.Business.DTOs.ConversationChunk;

namespace TradingApp.Infrastructure.Interfaces
{
    public interface IConversationContextArbiterService
    {
        Task<List<CreatedConversationChunkResponseDTO>> DetermineSufficientChunksAsync(
            string userQuestion, List<CreatedConversationChunkResponseDTO> existingChunks);
    }
}
