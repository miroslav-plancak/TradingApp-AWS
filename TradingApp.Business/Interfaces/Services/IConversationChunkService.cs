using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.ConversationChunk;

namespace TradingApp.Business.Interfaces.Services
{
    public interface IConversationChunkService
    {
        Task CreateConversationChunkAsync(List<CreateConversationChunkRequestDTO> request);
    }
}
