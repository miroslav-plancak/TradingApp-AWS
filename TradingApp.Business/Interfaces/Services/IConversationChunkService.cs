using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.ConversationChunk;

namespace TradingApp.Business.Interfaces.Services
{
    public interface IConversationChunkService
    {
        Task CreateConversationChunksAsync(List<CreateConversationChunkRequestDTO> request);
        Task<List<CreatedConversationChunkResponseDTO>> GetConversationChunksAsync(Guid conversationId);
    }
}
