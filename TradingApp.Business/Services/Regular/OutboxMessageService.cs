using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Outbox;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Business.Mappers;

namespace TradingApp.Business.Services.Regular
{
    public class OutboxMessageService : IOutboxMessageService
    {
        private readonly IOutboxMessageRepository _outboxMessageRepository;
        private readonly ILogger<OutboxMessageService> _logger;

        public OutboxMessageService(
            ILogger<OutboxMessageService> logger,
            IOutboxMessageRepository outboxMessageRepository)
        {
            _logger = logger;
            _outboxMessageRepository = outboxMessageRepository;
        }

        public async Task<OutboxMessageResponseDTO> GetByIdAsync(Guid id)
        {
            _logger.LogInformation("GetOutboxMessageById | Id: {Id}", id);

            try
            {
                var entity = await _outboxMessageRepository.GetByIdAsync(id);

                if (entity == null)
                {
                    _logger.LogWarning("OutboxMessageNotFound | Id: {Id}", id);
                    throw new KeyNotFoundException($"Outbox message {id} not found.");
                }

                var dto = OutboxMessageMapper.ToOutboxMessageResponseDTO(entity);

                _logger.LogInformation("OutboxMessageRetrieved | Id: {Id}", id);

                return dto;
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetOutboxMessageByIdFailed | Id: {Id}", id);
                throw new Exception($"Failed to retrieve outbox message {id}", ex);
            }
        }

        public async Task<IEnumerable<OutboxMessageResponseDTO>> GetAllAsync()
        {
            _logger.LogInformation("GetAllOutboxMessages");

            try
            {
                var entities = await _outboxMessageRepository.GetAllAsync();
                var dtos = OutboxMessageMapper.ToOutboxMessageResponseDTOs(entities);

                _logger.LogInformation("OutboxMessagesRetrieved | Count: {Count}", dtos.Count());

                return dtos;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetAllOutboxMessagesFailed");
                throw new Exception("Failed to retrieve outbox messages", ex);
            }
        }

        public async Task<IEnumerable<OutboxMessageResponseDTO>> GetUnprocessedAsync()
        {
            _logger.LogInformation("GetUnprocessedOutboxMessages");

            try
            {
                var entities = await _outboxMessageRepository.GetUnprocessedAsync();
                var dtos = OutboxMessageMapper.ToOutboxMessageResponseDTOs(entities);

                _logger.LogInformation("UnprocessedOutboxMessagesRetrieved | Count: {Count}", dtos.Count());

                return dtos;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetUnprocessedOutboxMessagesFailed");
                throw new Exception("Failed to retrieve unprocessed outbox messages", ex);
            }
        }

        public async Task<IEnumerable<OutboxMessageResponseDTO>> GetProcessedAsync()
        {
            _logger.LogInformation("GetProcessedOutboxMessages");

            try
            {
                var entities = await _outboxMessageRepository.GetProcessedAsync();
                var dtos = OutboxMessageMapper.ToOutboxMessageResponseDTOs(entities);

                _logger.LogInformation("ProcessedOutboxMessagesRetrieved | Count: {Count}", dtos.Count());

                return dtos;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetProcessedOutboxMessagesFailed");
                throw new Exception("Failed to retrieve processed outbox messages", ex);
            }
        }

        public async Task<OutboxMessageResponseDTO> MarkAsProcessedAsync(Guid id)
        {
            _logger.LogInformation("MarkOutboxMessageAsProcessed | Id: {Id}", id);

            try
            {
                var entity = await _outboxMessageRepository.MarkAsProcessedAsync(id);

                if (entity == null)
                {
                    _logger.LogWarning("OutboxMessageNotFoundForMarkProcessed | Id: {Id}", id);
                    throw new KeyNotFoundException($"Outbox message {id} not found.");
                }

                var dto = OutboxMessageMapper.ToOutboxMessageResponseDTO(entity);

                _logger.LogInformation("OutboxMessageMarkedAsProcessed | Id: {Id}", id);

                return dto;
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MarkOutboxMessageAsProcessedFailed | Id: {Id}", id);
                throw new Exception($"Failed to mark outbox message {id} as processed", ex);
            }
        }

        public async Task<bool> DeleteAsync(Guid id)
        {
            _logger.LogInformation("DeleteOutboxMessage | Id: {Id}", id);

            try
            {
                var entity = await _outboxMessageRepository.GetByIdAsync(id);

                if (entity == null)
                {
                    _logger.LogWarning("OutboxMessageNotFoundForDeletion | Id: {Id}", id);
                    throw new KeyNotFoundException($"Outbox message {id} not found.");
                }

                var deleted = await _outboxMessageRepository.DeleteAsync(id);

                _logger.LogInformation("OutboxMessageDeleted | Id: {Id}", id);

                return deleted;
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeleteOutboxMessageFailed | Id: {Id}", id);
                throw new Exception($"Failed to delete outbox message {id}", ex);
            }
        }

        public async Task<int> DeleteAllAsync()
        {
            _logger.LogInformation("DeleteAllOutboxMessages");

            try
            {
                var deletedCount = await _outboxMessageRepository.DeleteAllAsync();

                _logger.LogInformation("AllOutboxMessagesDeleted | Count: {Count}", deletedCount);

                return deletedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeleteAllOutboxMessagesFailed");
                throw new Exception("Failed to delete all outbox messages", ex);
            }
        }

        public async Task<OutboxMessageStatsDTO> GetStatsAsync()
        {
            _logger.LogInformation("GetOutboxMessageStats");

            try
            {
                var stats = await _outboxMessageRepository.GetStatsAsync();

                _logger.LogInformation("OutboxMessageStatsRetrieved");

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetOutboxMessageStatsFailed");
                throw new Exception("Failed to retrieve outbox message stats", ex);
            }
        }
    }
}
