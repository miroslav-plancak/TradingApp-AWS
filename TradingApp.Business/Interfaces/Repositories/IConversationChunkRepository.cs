using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Domain.Models.Entities.ConversationChunk;

namespace TradingApp.Business.Interfaces.Repositories
{
    public interface IConversationChunkRepository
    {
        Task<ConversationChunk> CreateConversationChunkAsync(ConversationChunk conversationChunk);
        Task<IEnumerable<ConversationChunk>> GetConversationChunksAsync(Guid conversationId);
    }
}
