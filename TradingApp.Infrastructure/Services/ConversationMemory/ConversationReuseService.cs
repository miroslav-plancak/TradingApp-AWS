using Microsoft.Extensions.Logging;
using TradingApp.Business.Interfaces.Services.Regular.Conversation;
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
        private readonly IConversationFullFileArbiterService _conversationFullFileArbiterService;

        public ConversationReuseService
        (
            ILogger<ConversationReuseService> logger,
            IConversationChunkService conversationChunkService,
            IConversationChunkArbiterService conversationChunkArbiterService,
            IConversationFullFileService conversationFullFileService,
            IConversationFullFileArbiterService conversationFullFileArbiterService
        )
        {
            _logger = logger;
            _conversationChunkService = conversationChunkService;
            _conversationChunkArbiterService = conversationChunkArbiterService;
            _conversationFullFileService = conversationFullFileService;
            _conversationFullFileArbiterService = conversationFullFileArbiterService;
        }

        public async Task<ReusableConversationArtifacts> TryRetrieveReusableConversationArtifactsAsync
        (
            Guid conversationId,
            string userMessage
        )
        {
            try
            {
                var allExistingConversationChunks = await _conversationChunkService.GetConversationChunksAsync(conversationId);

                var reusableConversationChunks = await _conversationChunkArbiterService.DetermineSufficientChunksAsync(userMessage, allExistingConversationChunks);

                var allExistingFullFiles = await _conversationFullFileService.GetConversationFullFilesAsync(conversationId);

                var reusableFullFiles = await _conversationFullFileArbiterService.DetermineSufficientFullFilesAsync(userMessage,allExistingFullFiles);

                return new ReusableConversationArtifacts()
                {
                    ConversationChunks = reusableConversationChunks,
                    ConversationFullFiles = reusableFullFiles
                };

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure occurred while retrieving conversation artifacts for the question: {UserMessage}", userMessage);
                return new ReusableConversationArtifacts() { ConversationChunks = [], ConversationFullFiles = [] };
            }
        }

        public async Task TryPersistReusableConversationArtifactsAsync
        (
            Guid conversationId,
            List<RetrievedChunk>? retrievedChunks,
            Dictionary<string, string>? fullFileContents
        )
        {
            try
            {
                await _conversationChunkService.CreateConversationChunksAsync(
                    RetrievalResultMapping.ToCreateConversationChunkRequestDTOs(retrievedChunks ?? [], conversationId));

                await _conversationFullFileService.CreateConversationFullFilesAsync(
                    RetrievalResultMapping.ToCreateConversationFullFileRequestDTOs(fullFileContents ?? [], conversationId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure occurred while attempting to persist artifacts for conversationId: {ConversationId}", conversationId);
            }
        }
    }
}
