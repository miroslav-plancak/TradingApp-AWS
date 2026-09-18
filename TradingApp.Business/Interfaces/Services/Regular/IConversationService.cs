using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Conversation;
using TradingApp.Business.DTOs.ConversationMessage;

namespace TradingApp.Business.Interfaces.Services.Regular
{   //TODO: split these into dedicated services
    public interface IConversationService
    {
        //#1 conversation based responsibility
        Task<CreatedConversationResponseDTO> CreateConversationAsync(string userMessage, Guid? clientRequestId);
        Task<List<CreatedConversationResponseDTO>> GetConversationsAsync();
        Task<CreatedConversationResponseDTO> GetConversationByIdAsync(Guid conversationId);
        Task<ConversationCompactionStateDTO> GetConversationCompactionStateAsync(Guid conversationId);
        Task<bool> DeleteConversationByIdAsync(Guid conversationId);
        //#2 conversationMessage based responsibility
        Task<CreatedConversationMessageResponseDTO> CreateConversationMessageAsync(CreateConversationMessageRequestDTO request);
        Task<List<ConversationHistoryMessageDTO>> GetConversationMessagesAsync(Guid conversationId, DateTimeOffset? createdAfter);
        //#3 conversationSummary responsibility
        Task UpdateCompactedConversationSummaryAsync(Guid conversationId, string compactedSummary, DateTimeOffset lastMessageCoveredBySummary);
    }
}