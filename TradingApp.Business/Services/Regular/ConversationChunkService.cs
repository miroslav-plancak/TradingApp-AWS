using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.ConversationChunk;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Business.Mappers;

namespace TradingApp.Business.Services.Regular
{
    public class ConversationChunkService : IConversationChunkService
    {
        private readonly IConversationChunkRepository _conversationChunkRepository;
        private readonly ILogger<ConversationChunkService> _logger;

        public ConversationChunkService
        (
            IConversationChunkRepository conversationChunkRepository,
            ILogger<ConversationChunkService> logger
        )
        {
            _conversationChunkRepository = conversationChunkRepository;
            _logger = logger;
        }

        public async Task CreateConversationChunksAsync(List<CreateConversationChunkRequestDTO> requests)
        {
            _logger.LogInformation("ConversationChunksCreationStarted | TotalRequests: {Requests}", requests.Count);

            try
            {
                if (requests.Count == 0) return;

                var existingConversationChunks = await _conversationChunkRepository
                    .GetConversationChunksAsync(requests.FirstOrDefault().ConversationId);

                var existingConvChunkKeys = existingConversationChunks.Select(x => x.Key).ToHashSet() ;

                if(existingConvChunkKeys.Count != 0)
                {
                    requests.RemoveAll(request => existingConvChunkKeys.Contains(request.Key));
                }

                var conversationChunkEntityRequests = ConversationChunkMapper.ToEntities(requests);

                if(conversationChunkEntityRequests.Count != 0)
                {
                    foreach( var request in conversationChunkEntityRequests)
                    {
                        var conversationChunk  = await _conversationChunkRepository.CreateConversationChunkAsync(request);

                        _logger.LogInformation("ConversationChunkCreationSuccessful | ConversationId: {ConversationId}", conversationChunk.Id);
                    }
                 
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConversationChunkCreationFailed  | Error: {Message}", ex.Message);
            }
        }

        public async Task<List<CreatedConversationChunkResultDTO>> GetConversationChunksAsync(Guid conversationId)
        {
            _logger.LogInformation("ConversationChunksFetchingStarted | ConversationId: {ConversationId}", conversationId);

            try
            {
                var conversationChunks = await _conversationChunkRepository.GetConversationChunksAsync(conversationId);

                var conversationChunkDTOs = ConversationChunkMapper.ToCreatedConversationChunkResultDTOs(conversationChunks);

                _logger.LogInformation("ConversationChunksFetchingSuccessful | ConversationId: {ConversationId}", conversationId);

                return conversationChunkDTOs;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConversationChunksFetchingFailed | returningEmptyResults  | Error: {Message}", ex.Message);
                return new List<CreatedConversationChunkResultDTO>();
                //NOTE: we will most likely remove this retrhwo because if we do not we risk this breaking the caller try/catch
                //throw new Exception("Failed to fetch conversation chunks messages", ex);
            }
        }
    }
}
