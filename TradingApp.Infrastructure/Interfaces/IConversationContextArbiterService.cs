using TradingApp.Business.DTOs.ConversationChunk;

namespace TradingApp.Infrastructure.Interfaces
{
    public interface IConversationContextArbiterService
    {
        Task<List<CreatedConversationChunkResultDTO>> DetermineSufficientChunksAsync(
            string userQuestion, List<CreatedConversationChunkResultDTO> existingChunks);
    }
}
