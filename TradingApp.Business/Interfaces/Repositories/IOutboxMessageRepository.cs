using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Outbox;
using TradingApp.Domain.Models.Entities.OutboxMessage;

namespace TradingApp.Business.Interfaces.Repositories
{
    public interface IOutboxMessageRepository
    {
        Task<OutboxMessage> GetByIdAsync(Guid id);
        Task<IEnumerable<OutboxMessage>> GetAllAsync();
        Task<IEnumerable<OutboxMessage>> GetUnprocessedAsync();
        Task<IEnumerable<OutboxMessage>> GetProcessedAsync();
        Task<OutboxMessage> MarkAsProcessedAsync(Guid id);
        Task<bool> DeleteAsync(Guid id);
        Task<int> DeleteAllAsync();
        Task<OutboxMessageStatsDTO> GetStatsAsync();
    }
}
