using TradingApp.Business.DTOs.ConversationFullFile;

namespace TradingApp.Infrastructure.Interfaces.ConversationMemory
{
    public interface IConversationFullFileArbiterService
    {
        Task<List<CreatedConversationFullFileResponseDTO>> DetermineSufficientFullFilesAsync(string userMessage,
            List<CreatedConversationFullFileResponseDTO> existingFullFiles);
    }
}
