using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Conversation;
using TradingApp.Business.DTOs.ConversationMessage;

namespace TradingApp.Business.Interfaces.Services
{
    public interface IConversationService
    {
        Task<CreatedConversationResponseDTO> CreateConversationAsync (string userMessage,Guid? clientRequestId);
        Task<List<CreatedConversationResponseDTO>> GetConversationsAsync();
        Task<CreatedConversationResponseDTO> GetConversationByIdAsync(Guid conversationId);
        Task<ConversationCompactionStateDTO> GetConversationCompactionStateAsync(Guid conversationId);
        Task<bool> DeleteConversationByIdAsync(Guid conversationId);
        Task<CreatedConversationMessageResponseDTO> CreateConversationMessageAsync(CreateConversationMessageRequestDTO request);
        Task<List<ConversationHistoryMessageDTO>> GetConversationMessagesAsync(Guid conversationId, DateTimeOffset? createdAfter);
        Task<List<ConversationHistoryMessageDTO>> GetConversationMessagesForCompactionAsync
        (
            ConversationCompactionStateDTO conversationDTO, 
            int postCompactionTokenBudget
        );
        Task UpdateCompactedConversationSummaryAsync(Guid conversationId, string compactedSummary, DateTimeOffset lastMessageCoveredBySummary);
    }
}