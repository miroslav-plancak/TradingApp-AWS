using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApp.Business.DTOs;
using TradingApp.Business.DTOs.DeadLetter;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.DeadLetterLog;

namespace TradingApp.Business.Repositories
{
    public class DeadLetterRepository : IDeadLetterRepository
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly ILogger<DeadLetterRepository> _logger;

        public DeadLetterRepository(ILogger<DeadLetterRepository> logger, TradingDbContext tradingDbContext)
        {
            _logger = logger;
            _tradingDbContext = tradingDbContext;
        }

        public async Task<DeadLetterLog> CreateDeadLetterLogAsync(DeadLetterLog deadLetterLog)
        {
            try
            {
                deadLetterLog.Id = Guid.NewGuid();
                deadLetterLog.CreatedAt = DateTimeOffset.UtcNow;
                deadLetterLog.IsResolved = false;

                _tradingDbContext.DeadLetterLogs.Add(deadLetterLog);
                await _tradingDbContext.SaveChangesAsync();

                return deadLetterLog;
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx,
                    "DatabaseError | Failed to create dead letter log | ClientOrderId: {ClientOrderId}",
                    deadLetterLog.ClientOrderId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "UnexpectedError | Failed to create dead letter log | ClientOrderId: {ClientOrderId}",
                    deadLetterLog.ClientOrderId);
                throw;
            }
        }

        public async Task<DeadLetterLog> GetDeadLetterLogByIdAsync(Guid id)
        {
            try
            {
                var result = await _tradingDbContext.DeadLetterLogs
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == id);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "DatabaseError | Failed to get dead letter log | Id: {Id}",
                    id);
                throw;
            }
        }

        public async Task<IEnumerable<DeadLetterLog>> GetAllDeadLetterLogsAsync()
        {
            try
            {
                var result = await _tradingDbContext.DeadLetterLogs
                    .OrderByDescending(x => x.CreatedAt)
                    .ToListAsync();

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DatabaseError | Failed to retrieve all dead letter logs");
                throw;
            }
        }

        public async Task<IEnumerable<DeadLetterLog>> GetUnresolvedDeadLetterLogsAsync()
        {
            try
            {
                var result = await _tradingDbContext.DeadLetterLogs
                    .Where(x => !x.IsResolved)
                    .OrderByDescending(x => x.CreatedAt)
                    .ToListAsync();

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DatabaseError | Failed to retrieve unresolved dead letter logs");
                throw;
            }
        }

        public async Task<DeadLetterLog> MarkAsResolvedAsync(Guid id, string resolutionNotes, string resolvedBy)
        {
            try
            {
                var deadLetterLog = await _tradingDbContext.DeadLetterLogs
                    .SingleOrDefaultAsync(x => x.Id == id);

                if (deadLetterLog == null)
                {
                    return null;
                }

                deadLetterLog.IsResolved = true;
                deadLetterLog.ResolutionNotes = resolutionNotes;
                deadLetterLog.ResolvedBy = resolvedBy;
                deadLetterLog.ResolvedAt = DateTimeOffset.UtcNow;

                await _tradingDbContext.SaveChangesAsync();

                return deadLetterLog;
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx,
                    "DatabaseError | Failed to mark dead letter log as resolved | Id: {Id}",
                    id);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "UnexpectedError | Failed to mark dead letter log as resolved | Id: {Id}",
                    id);
                throw;
            }
        }

        public async Task<DeadLetterLog> GetByClientOrderIdAsync(Guid clientOrderId)
        {
            try
            {
                var result = await _tradingDbContext.DeadLetterLogs
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.ClientOrderId == clientOrderId);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "DatabaseError | Failed to get dead letter log | ClientOrderId: {ClientOrderId}",
                    clientOrderId);
                throw;
            }
        }

        public async Task<DeadLetterStatsDTO> GetStatsAsync()
        {
            try
            {
                var allDeadLetters = await _tradingDbContext.DeadLetterLogs.ToListAsync();

                var stats = new DeadLetterStatsDTO
                {
                    TotalCount = allDeadLetters.Count,
                    UnresolvedCount = allDeadLetters.Count(x => !x.IsResolved),
                    ResolvedCount = allDeadLetters.Count(x => x.IsResolved),
                    Last24Hours = allDeadLetters.Count(x => x.CreatedAt >= DateTimeOffset.UtcNow.AddHours(-24))
                };

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DatabaseError | Failed to retrieve dead letter stats");
                throw;
            }
        }

        public async Task MarkOutboxMessageAsProcessedAsync(Guid clientOrderId)
        {
            try
            {
                var outboxMessage = await _tradingDbContext.OutboxMessages
                    .FirstOrDefaultAsync(x =>
                        x.Payload == clientOrderId.ToString() &&
                        x.ProcessedAt == null);

                if (outboxMessage != null)
                {
                    outboxMessage.ProcessedAt = DateTimeOffset.UtcNow;
                    await _tradingDbContext.SaveChangesAsync();
                }
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx,
                    "DatabaseError | Failed to mark outbox message as processed | ClientOrderId: {ClientOrderId}",
                    clientOrderId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "UnexpectedError | Failed to mark outbox message as processed | ClientOrderId: {ClientOrderId}",
                    clientOrderId);
                throw;
            }
        }

        public async Task<bool> DeleteDeadLetterLogAsync(Guid id)
        {
            try
            {
                var deadLetterLog = await _tradingDbContext.DeadLetterLogs
                    .SingleOrDefaultAsync(x => x.Id == id);

                if (deadLetterLog == null)
                {
                    return false;
                }

                _tradingDbContext.DeadLetterLogs.Remove(deadLetterLog);
                await _tradingDbContext.SaveChangesAsync();

                return true;
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx,
                    "DatabaseError | Failed to delete dead letter log | Id: {Id}",
                    id);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "UnexpectedError | Failed to delete dead letter log | Id: {Id}",
                    id);
                throw;
            }
        }

        public async Task<int> DeleteAllDeadLetterLogsAsync()
        {
            try
            {
                var deadLetterLogs = await _tradingDbContext.DeadLetterLogs.ToListAsync();
                var count = deadLetterLogs.Count;

                if (count > 0)
                {
                    _tradingDbContext.DeadLetterLogs.RemoveRange(deadLetterLogs);
                    await _tradingDbContext.SaveChangesAsync();
                }

                return count;
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx, "DatabaseError | Failed to delete all dead letter logs");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UnexpectedError | Failed to delete all dead letter logs");
                throw;
            }
        }
    }
}
