using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.ConversationChunk;
using TradingApp.Business.DTOs.ConversationFullFile;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Business.Mappers;
using TradingApp.Domain.Models.Entities.ConversationFullFile;

namespace TradingApp.Business.Services.Regular
{
    public class ConversationFullFileService : IConversationFullFileService
    {
        private readonly IConversationFullFileRepository _conversationFullFileRepository;
        private readonly ILogger<ConversationFullFileService> _logger;

        public ConversationFullFileService
        (
            IConversationFullFileRepository conversationFullFileRepository,
            ILogger<ConversationFullFileService> logger
        )
        {
            _conversationFullFileRepository = conversationFullFileRepository;
            _logger = logger;
        }

        public async Task CreateConversationFullFilesAsync(List<CreateConversationFullFileRequestDTO> requests)
        {
            _logger.LogInformation("ConversationFullFilesCreationStarted | TotalRequests: {Requests}", requests.Count);

            try
            {
                if (requests.Count == 0) return;

                var existingConversationFullFiles = await _conversationFullFileRepository
                    .GetConversationFullFilesAsync(requests.FirstOrDefault().ConversationId);

                var existingConvChunkKeys = existingConversationFullFiles.Select(x => x.SourceFile).ToHashSet();

                if (existingConvChunkKeys.Count != 0)
                {
                    requests.RemoveAll(request => existingConvChunkKeys.Contains(request.SourceFile));
                }

                var conversationFullFileEntityRequests = ConversationFullFileMapper.ToEntities(requests);

                if (conversationFullFileEntityRequests.Count != 0)
                {
                    foreach (var request in conversationFullFileEntityRequests)
                    {
                        var conversationFullFile = await _conversationFullFileRepository.CreateConversationFullFileAsync(request);

                        _logger.LogInformation("ConversationFullFilesCreationSuccessful | ConversationId: {ConversationId}", conversationFullFile.Id);
                    }

                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConversationFullFilesCreationFailed  | Error: {Message}", ex.Message);
            }
        }

        public async Task<List<CreatedConversationFullFileResponseDTO>> GetConversationFullFilesAsync(Guid? conversationId)
        {
            _logger.LogInformation("ConversationFullFilesFetchingStarted | ConversationId: {ConversationId}", conversationId);

            try
            {
                var conversationFullFiles = await _conversationFullFileRepository.GetConversationFullFilesAsync(conversationId);

                var conversationFullFileDTOs = ConversationFullFileMapper.ToCreatedConversationFullFileResponseDTOs(conversationFullFiles);

                _logger.LogInformation("ConversationFullFilesFetchingSuccessful | ConversationId: {ConversationId}", conversationId);

                return conversationFullFileDTOs;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConversationFullFilesFetchingFailed | returningEmptyResults  | Error: {Message}", ex.Message);

                return new List<CreatedConversationFullFileResponseDTO>();
            }
        }

        public async Task<List<CreatedConversationFullFileResponseDTO>> GetSpecificConversationFullFilesAsync(List<CreatedConversationChunkResponseDTO> requests)
        {
            if (requests.Count == 0) return [];

            var conversationId = requests?.FirstOrDefault().ConversationId;

            _logger.LogInformation("ConversationFullFilesArbiterBasedFetchingStarted | ConversationId: {ConversationId}", conversationId);

            try
            {
                var requestedSourceFileNames = requests.GroupBy(x => x.SourceFile).Select(kvp => kvp.Key).ToList();

                var allExistingConversationFullFiles = await GetConversationFullFilesAsync(conversationId);

                var matchedFullFileDtos = allExistingConversationFullFiles
                    .Where(x => requestedSourceFileNames.Contains(x.SourceFile))
                    .ToList();

                _logger.LogInformation("ConversationFullFilesArbiterBasedFetchingSuccessful | ConversationId: {ConversationId}", conversationId);

                return matchedFullFileDtos;
              
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConversationFullFilesArbiterBasedFetchingFailed | returningEmptyResults  | Error: {Message}", ex.Message);

                return new List<CreatedConversationFullFileResponseDTO>();
            }
        }

    }
}
