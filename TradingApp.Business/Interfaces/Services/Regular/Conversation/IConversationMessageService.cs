using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.ConversationMessage;

namespace TradingApp.Business.Interfaces.Services.Regular.Conversation
{
    public interface IConversationMessageService
    {
        Task<CreatedConversationMessageResponseDTO> CreateConversationMessageAsync(CreateConversationMessageRequestDTO request);
        Task<List<ConversationHistoryMessageDTO>> GetConversationMessagesAsync(Guid conversationId, DateTimeOffset? createdAfter);
    }
}
