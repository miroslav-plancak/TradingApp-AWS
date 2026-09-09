using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Domain.Models.Entities.ConversationFullFile;

namespace TradingApp.Business.Interfaces.Repositories
{
    public interface IConversationFullFileRepository
    {
        Task<ConversationFullFile> CreateConversationFullFileAsync(ConversationFullFile conversationFullFile);
        Task<IEnumerable<ConversationFullFile>> GetConversationFullFilesAsync(Guid? conversationId);
    }
}
