using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.ConversationFullFile;

namespace TradingApp.Business.Interfaces.Services.Regular.Conversation
{
    public interface IConversationFullFileService
    {
        Task CreateConversationFullFilesAsync(List<CreateConversationFullFileRequestDTO> requests);
        Task<List<CreatedConversationFullFileResponseDTO>> GetConversationFullFilesAsync(Guid? conversationId, CancellationToken ct = default);
    }
}
