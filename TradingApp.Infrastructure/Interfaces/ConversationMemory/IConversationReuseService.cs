using TradingApp.Infrastructure.Models.ConversationMemory;
using TradingApp.Infrastructure.Models.Retrieval;
using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Interfaces.ConversationMemory
{
    public interface IConversationReuseService
    {
        Task<ReusableConversationArtifacts> TryRetrieveReusableConversationArtifactsAsync
        (
            Guid conversationId,
            string userQuery
        );
        Task TryPersistReusableConversationArtifactsAsync
        (
            Guid conversationId,
            List<RetrievedChunk> retrievedChunks,
            Dictionary<string, string> fullFileContents
        );
    }
}
