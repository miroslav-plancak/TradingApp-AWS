using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Conversation;
using TradingApp.Business.DTOs.ConversationMessage;

namespace TradingApp.Business.Interfaces.Services.Regular.Conversation
{
    public interface IConversationCompactionBoundaryService
    {
        Task<List<ConversationHistoryMessageDTO>> GetMessagesForCompactionAsync
        (
            ConversationCompactionStateDTO conversationDTO,
            int postCompactionTokenBudget
        );
    }
}
