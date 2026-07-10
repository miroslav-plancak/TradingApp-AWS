using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.DeadLetter;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Business.Mappers;

namespace TradingApp.Business.Services.Regular
{
    public class DeadLetterService : IDeadLetterService
    {
        private readonly IDeadLetterRepository _deadLetterRepository;
        private readonly ILogger<DeadLetterService> _logger;

        public DeadLetterService(
            ILogger<DeadLetterService> logger,
            IDeadLetterRepository deadLetterRepository)
        {
            _logger = logger;
            _deadLetterRepository = deadLetterRepository;
        }

        public Task<DeadLetterLogResponseDTO> CreateDeadLetterLogAsync(string messageBody, Guid clientOrderId, string reason)
        {
            return CreateDeadLetterLogAsync(messageBody, clientOrderId, reason, null);
        }

        public Task<DeadLetterLogResponseDTO> CreateDeadLetterLogAsync(string messageBody, Guid clientOrderId, string reason, string correlationId)
        {
            return CreateDeadLetterLogAsync(new CreateDeadLetterRequestDTO
            {
                MessageBody = messageBody,
                ClientOrderId = clientOrderId,
                Reason = reason,
                CorrelationId = correlationId
            });
        }

        public async Task<DeadLetterLogResponseDTO> CreateDeadLetterLogAsync(CreateDeadLetterRequestDTO createRequest)
        {
            _logger.LogInformation("CreateDeadLetterLog | CorrelationId: {CorrelationId} | ClientOrderId: {ClientOrderId}", createRequest.CorrelationId, createRequest.ClientOrderId);

            try
            {
                var deadLetterEntity = DeadLetterMapper.ToEntity(createRequest.MessageBody, createRequest.ClientOrderId, createRequest.Reason, createRequest.CorrelationId);
                var deadLetterLog = await _deadLetterRepository.CreateDeadLetterLogAsync(deadLetterEntity);

                _logger.LogInformation("DeadLetterLogCreated | CorrelationId: {CorrelationId} | Id: {Id} | ClientOrderId: {ClientOrderId}",
                    createRequest.CorrelationId, deadLetterLog.Id, createRequest.ClientOrderId);

                return DeadLetterMapper.ToDeadLetterLogResponseDTO(deadLetterLog);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CreateDeadLetterLogFailed | CorrelationId: {CorrelationId} | ClientOrderId: {ClientOrderId}", createRequest.CorrelationId, createRequest.ClientOrderId);
                throw new Exception($"Failed to create dead letter log for client order {createRequest.ClientOrderId}", ex);
            }
        }

        public async Task<DeadLetterLogResponseDTO> GetDeadLetterLogByIdAsync(Guid id)
        {
            _logger.LogInformation("GetDeadLetterLogById | Id: {Id}", id);

            try
            {
                var deadLetterLog = await _deadLetterRepository.GetDeadLetterLogByIdAsync(id);

                if (deadLetterLog == null)
                {
                    _logger.LogWarning("DeadLetterLogNotFound | Id: {Id}", id);
                    throw new KeyNotFoundException($"Dead letter log {id} not found.");
                }

                var deadLetterDTO = DeadLetterMapper.ToDeadLetterLogResponseDTO(deadLetterLog);

                _logger.LogInformation("DeadLetterLogRetrieved | Id: {Id}", id);

                return deadLetterDTO;
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetDeadLetterLogByIdFailed | Id: {Id}", id);
                throw new Exception($"Failed to retrieve dead letter log {id}", ex);
            }
        }

        public async Task<DeadLetterLogResponseDTO> GetByClientOrderIdAsync(Guid clientOrderId)
        {
            _logger.LogInformation("GetDeadLetterLogByClientOrderId | ClientOrderId: {ClientOrderId}", clientOrderId);

            try
            {
                var deadLetterLog = await _deadLetterRepository.GetByClientOrderIdAsync(clientOrderId);

                if (deadLetterLog == null)
                {
                    _logger.LogWarning("DeadLetterLogNotFoundForClientOrder | ClientOrderId: {ClientOrderId}", clientOrderId);
                    throw new KeyNotFoundException($"Dead letter log for client order {clientOrderId} not found.");
                }

                var deadLetterDTO = DeadLetterMapper.ToDeadLetterLogResponseDTO(deadLetterLog);

                _logger.LogInformation("DeadLetterLogRetrievedByClientOrderId | ClientOrderId: {ClientOrderId}", clientOrderId);

                return deadLetterDTO;
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetDeadLetterLogByClientOrderIdFailed | ClientOrderId: {ClientOrderId}", clientOrderId);
                throw new Exception($"Failed to retrieve dead letter log for client order {clientOrderId}", ex);
            }
        }

        public async Task<IEnumerable<DeadLetterLogResponseDTO>> GetAllDeadLetterLogsAsync()
        {
            _logger.LogInformation("GetAllDeadLetterLogs");

            try
            {
                var deadLetterLogs = await _deadLetterRepository.GetAllDeadLetterLogsAsync();
                var deadLetterDTOs = DeadLetterMapper.ToDeadLetterLogResponseDTOs(deadLetterLogs);

                _logger.LogInformation("DeadLetterLogsRetrieved | Count: {Count}", deadLetterDTOs.Count());

                return deadLetterDTOs;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetAllDeadLetterLogsFailed");
                throw new Exception("Failed to retrieve dead letter logs", ex);
            }
        }

        public async Task<IEnumerable<DeadLetterLogResponseDTO>> GetUnresolvedDeadLetterLogsAsync()
        {
            _logger.LogInformation("GetUnresolvedDeadLetterLogs");

            try
            {
                var deadLetterLogs = await _deadLetterRepository.GetUnresolvedDeadLetterLogsAsync();
                var deadLetterDTOs = DeadLetterMapper.ToDeadLetterLogResponseDTOs(deadLetterLogs);

                _logger.LogInformation("UnresolvedDeadLetterLogsRetrieved | Count: {Count}", deadLetterDTOs.Count());

                return deadLetterDTOs;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetUnresolvedDeadLetterLogsFailed");
                throw new Exception("Failed to retrieve unresolved dead letter logs", ex);
            }
        }

        public async Task<DeadLetterLogResponseDTO> MarkAsResolvedAsync(Guid id, ResolveDeadLetterRequestDTO resolveRequest)
        {
            _logger.LogInformation("MarkDeadLetterLogAsResolved | Id: {Id}", id);

            try
            {
                var deadLetterLog = await _deadLetterRepository.MarkAsResolvedAsync(
                    id,
                    resolveRequest.ResolutionNotes,
                    resolveRequest.ResolvedBy);

                if (deadLetterLog == null)
                {
                    _logger.LogWarning("DeadLetterLogNotFoundForResolve | Id: {Id}", id);
                    throw new KeyNotFoundException($"Dead letter log {id} not found.");
                }

                var deadLetterDTO = DeadLetterMapper.ToDeadLetterLogResponseDTO(deadLetterLog);

                _logger.LogInformation("DeadLetterLogMarkedAsResolved | Id: {Id}", id);

                return deadLetterDTO;
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MarkDeadLetterLogAsResolvedFailed | Id: {Id}", id);
                throw new Exception($"Failed to mark dead letter log {id} as resolved", ex);
            }
        }

        public async Task<DeadLetterStatsDTO> GetStatsAsync()
        {
            _logger.LogInformation("GetDeadLetterStats");

            try
            {
                var stats = await _deadLetterRepository.GetStatsAsync();

                _logger.LogInformation("DeadLetterStatsRetrieved");

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetDeadLetterStatsFailed");
                throw new Exception("Failed to retrieve dead letter stats", ex);
            }
        }

        public async Task<bool> DeleteDeadLetterLogAsync(Guid id)
        {
            _logger.LogInformation("DeleteDeadLetterLog | Id: {Id}", id);

            try
            {
                var deadLetterLog = await _deadLetterRepository.GetDeadLetterLogByIdAsync(id);

                if (deadLetterLog == null)
                {
                    _logger.LogWarning("DeadLetterLogNotFoundForDeletion | Id: {Id}", id);
                    throw new KeyNotFoundException($"Dead letter log {id} not found.");
                }

                var deleted = await _deadLetterRepository.DeleteDeadLetterLogAsync(id);

                _logger.LogInformation("DeadLetterLogDeleted | Id: {Id}", id);

                return deleted;
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeleteDeadLetterLogFailed | Id: {Id}", id);
                throw new Exception($"Failed to delete dead letter log {id}", ex);
            }
        }

        public async Task<int> DeleteAllDeadLetterLogsAsync()
        {
            _logger.LogInformation("DeleteAllDeadLetterLogs");

            try
            {
                var deletedCount = await _deadLetterRepository.DeleteAllDeadLetterLogsAsync();

                _logger.LogInformation("AllDeadLetterLogsDeleted | Count: {Count}", deletedCount);

                return deletedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeleteAllDeadLetterLogsFailed");
                throw new Exception("Failed to delete all dead letter logs", ex);
            }
        }

        public async Task MarkOutboxMessageAsProcessedAsync(Guid clientOrderId)
        {
            _logger.LogInformation("MarkOutboxMessageAsProcessed | ClientOrderId: {ClientOrderId}", clientOrderId);

            try
            {
                await _deadLetterRepository.MarkOutboxMessageAsProcessedAsync(clientOrderId);

                _logger.LogInformation("OutboxMessageMarkedAsProcessed | ClientOrderId: {ClientOrderId}", clientOrderId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MarkOutboxMessageAsProcessedFailed | ClientOrderId: {ClientOrderId}", clientOrderId);
                throw new Exception($"Failed to mark outbox message as processed for client order {clientOrderId}", ex);
            }
        }
    }
}
