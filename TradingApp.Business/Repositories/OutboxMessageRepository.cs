using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Outbox;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.OutboxMessage;

namespace TradingApp.Business.Repositories
{
    public class OutboxMessageRepository : IOutboxMessageRepository
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly ILogger<OutboxMessageRepository> _logger;

        public OutboxMessageRepository(ILogger<OutboxMessageRepository> logger, TradingDbContext tradingDbContext)
        {
            _logger = logger;
            _tradingDbContext = tradingDbContext;
        }

        public async Task<OutboxMessage> GetByIdAsync(Guid id)
        {
            try
            {
                var result = await _tradingDbContext.OutboxMessages
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == id);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "DatabaseError | Failed to get outbox message | Id: {Id}",
                    id);
                throw;
            }
        }

        public async Task<IEnumerable<OutboxMessage>> GetAllAsync()
        {
            try
            {
                var result = await _tradingDbContext.OutboxMessages
                    .AsNoTracking()
                    .OrderByDescending(x => x.CreatedAt)
                    .ToListAsync();

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DatabaseError | Failed to retrieve all outbox messages");
                throw;
            }
        }

        public async Task<IEnumerable<OutboxMessage>> GetUnprocessedAsync()
        {
            try
            {
                var result = await _tradingDbContext.OutboxMessages
                    .AsNoTracking()
                    .Where(x => x.ProcessedAt == null)
                    .OrderByDescending(x => x.CreatedAt)
                    .ToListAsync();

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DatabaseError | Failed to retrieve unprocessed outbox messages");
                throw;
            }
        }

        public async Task<IEnumerable<OutboxMessage>> GetProcessedAsync()
        {
            try
            {
                var result = await _tradingDbContext.OutboxMessages
                    .AsNoTracking()
                    .Where(x => x.ProcessedAt != null)
                    .OrderByDescending(x => x.CreatedAt)
                    .ToListAsync();

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DatabaseError | Failed to retrieve processed outbox messages");
                throw;
            }
        }

        public async Task<OutboxMessage> MarkAsProcessedAsync(Guid id)
        {
            try
            {
                var outboxMessage = await _tradingDbContext.OutboxMessages
                    .SingleOrDefaultAsync(x => x.Id == id);

                if (outboxMessage == null)
                {
                    return null;
                }

                outboxMessage.ProcessedAt = DateTimeOffset.UtcNow;
                await _tradingDbContext.SaveChangesAsync();

                return outboxMessage;
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx,
                    "DatabaseError | Failed to mark outbox message as processed | Id: {Id}",
                    id);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "UnexpectedError | Failed to mark outbox message as processed | Id: {Id}",
                    id);
                throw;
            }
        }

        public async Task<bool> DeleteAsync(Guid id)
        {
            try
            {
                var outboxMessage = await _tradingDbContext.OutboxMessages
                    .SingleOrDefaultAsync(x => x.Id == id);

                if (outboxMessage == null)
                {
                    return false;
                }

                _tradingDbContext.OutboxMessages.Remove(outboxMessage);
                await _tradingDbContext.SaveChangesAsync();

                return true;
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx,
                    "DatabaseError | Failed to delete outbox message | Id: {Id}",
                    id);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "UnexpectedError | Failed to delete outbox message | Id: {Id}",
                    id);
                throw;
            }
        }

        public async Task<int> DeleteAllAsync()
        {
            try
            {
                var outboxMessages = await _tradingDbContext.OutboxMessages.ToListAsync();
                var count = outboxMessages.Count;

                if (count > 0)
                {
                    _tradingDbContext.OutboxMessages.RemoveRange(outboxMessages);
                    await _tradingDbContext.SaveChangesAsync();
                }

                return count;
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx, "DatabaseError | Failed to delete all outbox messages");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UnexpectedError | Failed to delete all outbox messages");
                throw;
            }
        }

        public async Task<OutboxMessageStatsDTO> GetStatsAsync()
        {
            try
            {
                var allMessages = await _tradingDbContext.OutboxMessages
                    .AsNoTracking()
                    .ToListAsync();

                var stats = new OutboxMessageStatsDTO
                {
                    TotalCount = allMessages.Count,
                    ProcessedCount = allMessages.Count(x => x.ProcessedAt.HasValue),
                    UnprocessedCount = allMessages.Count(x => !x.ProcessedAt.HasValue),
                    Last24Hours = allMessages.Count(x => x.CreatedAt >= DateTimeOffset.UtcNow.AddHours(-24))
                };

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DatabaseError | Failed to retrieve outbox message stats");
                throw;
            }
        }
    }
}
