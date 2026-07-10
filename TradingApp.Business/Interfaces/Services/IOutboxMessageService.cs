using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Outbox;

namespace TradingApp.Business.Interfaces.Services
{
    public interface IOutboxMessageService
    {
        Task<OutboxMessageResponseDTO> GetByIdAsync(Guid id);
        Task<IEnumerable<OutboxMessageResponseDTO>> GetAllAsync();
        Task<IEnumerable<OutboxMessageResponseDTO>> GetUnprocessedAsync();
        Task<IEnumerable<OutboxMessageResponseDTO>> GetProcessedAsync();
        Task<OutboxMessageResponseDTO> MarkAsProcessedAsync(Guid id);
        Task<bool> DeleteAsync(Guid id);
        Task<int> DeleteAllAsync();
        Task<OutboxMessageStatsDTO> GetStatsAsync();
    }
}
