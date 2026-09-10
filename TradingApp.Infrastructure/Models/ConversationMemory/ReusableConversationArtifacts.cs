using TradingApp.Business.DTOs.ConversationChunk;
using TradingApp.Business.DTOs.ConversationFullFile;

namespace TradingApp.Infrastructure.Models.ConversationMemory
{   
    public class ReusableConversationArtifacts
    {
        public List<CreatedConversationChunkResponseDTO> ConversationChunks { get; set; } = [];
        public List<CreatedConversationFullFileResponseDTO> ConversationFullFiles { get; set; } = [];
    }
}
