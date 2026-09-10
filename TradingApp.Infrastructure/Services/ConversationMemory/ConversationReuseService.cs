using Microsoft.Extensions.Logging;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Infrastructure.Helpers.Retrieval;
using TradingApp.Infrastructure.Interfaces.ConversationMemory;
using TradingApp.Infrastructure.Models.ConversationMemory;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Services.ConversationMemory
{
    public class ConversationReuseService : IConversationReuseService
    {
        private readonly ILogger<ConversationReuseService> _logger;
        private readonly IConversationChunkService _conversationChunkService;
        private readonly IConversationChunkArbiterService _conversationChunkArbiterService;
        private readonly IConversationFullFileService _conversationFullFileService;

        public ConversationReuseService
        (
            ILogger<ConversationReuseService> logger,
            IConversationChunkService conversationChunkService,
            IConversationChunkArbiterService conversationChunkArbiterService,
            IConversationFullFileService conversationFullFileService
        )
        {
            _logger = logger;
            _conversationChunkService = conversationChunkService;
            _conversationChunkArbiterService = conversationChunkArbiterService;
            _conversationFullFileService = conversationFullFileService;
        }

        public async Task<ReusableConversationArtifacts> TryRetrieveReusableConversationArtifactsAsync
        (
            Guid conversationId,
            string userQuery
        )
        {
            try 
            {
                var allExistingConversationChunks = await _conversationChunkService.GetConversationChunksAsync(conversationId);

                var reusableConversationChunks = await _conversationChunkArbiterService.DetermineSufficientChunksAsync(userQuery, allExistingConversationChunks);

                var reusableConversationFullFiles = await _conversationFullFileService.ResolveFullFilesForChunksAsync(reusableConversationChunks);

                return new ReusableConversationArtifacts() 
                { 
                    ConversationChunks = reusableConversationChunks, 
                    ConversationFullFiles = reusableConversationFullFiles 
                };

            } 
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure occurred while retrieving conversation artifacts for the question: {UserQuery}", userQuery);
                return new ReusableConversationArtifacts() { ConversationChunks = [], ConversationFullFiles = [] };
            }
        }

        public async Task TryPersistReusableConversationArtifactsAsync
        (
            Guid conversationId,
            List<RetrievedChunk> retrievedChunks,
            Dictionary<string, string> fullFileContents
        )
        {
            try 
            {
                await _conversationChunkService.CreateConversationChunksAsync(
                    RetrievalResultMapping.ToCreateConversationChunkRequestDTOs(retrievedChunks, conversationId));

                await _conversationFullFileService.CreateConversationFullFilesAsync(
                    RetrievalResultMapping.ToCreateConversationFullFileRequestDTOs(fullFileContents, conversationId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure occurred while attempting to persist artifacts for conversationId: {ConversationId}", conversationId);
            }
        }
    }
}
