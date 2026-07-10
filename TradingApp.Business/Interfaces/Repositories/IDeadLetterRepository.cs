using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.DeadLetter;
using TradingApp.Domain.Models.Entities.DeadLetterLog;

namespace TradingApp.Business.Interfaces.Repositories
{
    public interface IDeadLetterRepository
    {
        Task<DeadLetterLog> CreateDeadLetterLogAsync(DeadLetterLog deadLetterLog);
        Task<DeadLetterLog> GetDeadLetterLogByIdAsync(Guid id);
        Task<IEnumerable<DeadLetterLog>> GetAllDeadLetterLogsAsync();
        Task<IEnumerable<DeadLetterLog>> GetUnresolvedDeadLetterLogsAsync();
        Task<DeadLetterLog> MarkAsResolvedAsync(Guid id, string resolutionNotes, string resolvedBy);
        Task<DeadLetterLog> GetByClientOrderIdAsync(Guid clientOrderId);
        Task<DeadLetterStatsDTO> GetStatsAsync();
        Task MarkOutboxMessageAsProcessedAsync(Guid clientOrderId);
        Task<bool> DeleteDeadLetterLogAsync(Guid id);
        Task<int> DeleteAllDeadLetterLogsAsync();
    }
}