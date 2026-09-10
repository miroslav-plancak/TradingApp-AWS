using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.ConversationChunk;
using TradingApp.Business.DTOs.ConversationFullFile;

namespace TradingApp.Business.Interfaces.Services
{
    public interface IConversationFullFileService
    {
        Task CreateConversationFullFilesAsync(List<CreateConversationFullFileRequestDTO> requests);
        Task<List<CreatedConversationFullFileResponseDTO>> GetConversationFullFilesAsync(Guid? conversationId);
        Task<List<CreatedConversationFullFileResponseDTO>> ResolveFullFilesForChunksAsync(List<CreatedConversationChunkResponseDTO> reusableChunks);
    }
}
