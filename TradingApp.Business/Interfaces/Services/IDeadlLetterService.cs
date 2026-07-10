using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs;
using TradingApp.Business.DTOs.DeadLetter;

namespace TradingApp.Business.Interfaces.Services
{
    public interface IDeadLetterService
    {
        Task<DeadLetterLogResponseDTO> CreateDeadLetterLogAsync(string messageBody, Guid clientOrderId, string reason);
        Task<DeadLetterLogResponseDTO> CreateDeadLetterLogAsync(string messageBody, Guid clientOrderId, string reason, string correlationId);
        Task<DeadLetterLogResponseDTO> CreateDeadLetterLogAsync(CreateDeadLetterRequestDTO createRequest);
        Task<DeadLetterLogResponseDTO> GetDeadLetterLogByIdAsync(Guid id);
        Task<DeadLetterLogResponseDTO> GetByClientOrderIdAsync(Guid clientOrderId);
        Task<IEnumerable<DeadLetterLogResponseDTO>> GetAllDeadLetterLogsAsync();
        Task<IEnumerable<DeadLetterLogResponseDTO>> GetUnresolvedDeadLetterLogsAsync();
        Task<DeadLetterLogResponseDTO> MarkAsResolvedAsync(Guid id, ResolveDeadLetterRequestDTO resolveRequest);
        Task<DeadLetterStatsDTO> GetStatsAsync();
        Task MarkOutboxMessageAsProcessedAsync(Guid clientOrderId);
        Task<bool> DeleteDeadLetterLogAsync(Guid id);
        Task<int> DeleteAllDeadLetterLogsAsync();
    }
}