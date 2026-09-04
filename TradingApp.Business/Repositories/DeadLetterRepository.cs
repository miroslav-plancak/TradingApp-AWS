using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.DeadLetter;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services.Helpers;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.DeadLetterLog;

namespace TradingApp.Business.Repositories
{
    public class DeadLetterRepository : IDeadLetterRepository
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IResiliencePolicyGuard _resiliencePolicyGuard;

        public DeadLetterRepository
        (
            TradingDbContext tradingDbContext,
            IResiliencePolicyGuard resiliencePolicyGuard
        )
        {
            _tradingDbContext = tradingDbContext;
            _resiliencePolicyGuard = resiliencePolicyGuard;
        }

        public async Task<DeadLetterLog> CreateDeadLetterLogAsync(DeadLetterLog deadLetterLog)
        {
            await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
            {
                deadLetterLog.Id = Guid.NewGuid();
                deadLetterLog.CreatedAt = DateTimeOffset.UtcNow;
                deadLetterLog.IsResolved = false;

                _tradingDbContext.DeadLetterLogs.Add(deadLetterLog);
                await _tradingDbContext.SaveChangesAsync();
            },
            $"{nameof(CreateDeadLetterLogAsync)}:Save:{deadLetterLog.ClientOrderId}");

            return deadLetterLog;
        }

        public async Task<DeadLetterLog> GetDeadLetterLogByIdAsync(Guid id)
        {
            return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.DeadLetterLogs
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == id),
                $"{nameof(GetDeadLetterLogByIdAsync)}:Fetch:{id}");
        }

        public async Task<IEnumerable<DeadLetterLog>> GetAllDeadLetterLogsAsync()
        {
            return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.DeadLetterLogs
                    .AsNoTracking()
                    .OrderByDescending(x => x.CreatedAt)
                    .ToListAsync(),
                nameof(GetAllDeadLetterLogsAsync) + ":Fetch");
        }

        public async Task<IEnumerable<DeadLetterLog>> GetUnresolvedDeadLetterLogsAsync()
        {
            return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.DeadLetterLogs
                    .AsNoTracking()
                    .Where(x => !x.IsResolved)
                    .OrderByDescending(x => x.CreatedAt)
                    .ToListAsync(),
                nameof(GetUnresolvedDeadLetterLogsAsync) + ":Fetch");
        }

        public async Task<DeadLetterLog> MarkAsResolvedAsync(Guid id, string resolutionNotes, string resolvedBy)
        {
            var deadLetterLog = await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.DeadLetterLogs
                    .SingleOrDefaultAsync(x => x.Id == id),
                $"{nameof(MarkAsResolvedAsync)}:Fetch:{id}");

            if (deadLetterLog == null)
            {
                return null;
            }

            await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
            {
                deadLetterLog.IsResolved = true;
                deadLetterLog.ResolutionNotes = resolutionNotes;
                deadLetterLog.ResolvedBy = resolvedBy;
                deadLetterLog.ResolvedAt = DateTimeOffset.UtcNow;

                await _tradingDbContext.SaveChangesAsync();
            }, $"{nameof(MarkAsResolvedAsync)}:Save:{id}");

            return deadLetterLog;
        }

        public async Task<DeadLetterLog> GetByClientOrderIdAsync(Guid clientOrderId)
        {
            return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.DeadLetterLogs
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.ClientOrderId == clientOrderId),
                $"{nameof(GetByClientOrderIdAsync)}:Fetch:{clientOrderId}");
        }

        public async Task<DeadLetterStatsDTO> GetStatsAsync()
        {
            var allDeadLetters = await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.DeadLetterLogs.AsNoTracking().ToListAsync(),
                nameof(GetStatsAsync) + ":Fetch");

            var stats = new DeadLetterStatsDTO
            {
                TotalCount = allDeadLetters.Count,
                UnresolvedCount = allDeadLetters.Count(x => !x.IsResolved),
                ResolvedCount = allDeadLetters.Count(x => x.IsResolved),
                Last24Hours = allDeadLetters.Count(x => x.CreatedAt >= DateTimeOffset.UtcNow.AddHours(-24))
            };

            return stats;
        }

        public async Task MarkOutboxMessageAsProcessedAsync(Guid clientOrderId)
        {
            var outboxMessage = await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.OutboxMessages
                    .FirstOrDefaultAsync(x =>
                        x.Payload == clientOrderId.ToString() &&
                        x.ProcessedAt == null),
                $"{nameof(MarkOutboxMessageAsProcessedAsync)}:Fetch:{clientOrderId}");

            if (outboxMessage != null)
            {
                await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                {
                    outboxMessage.ProcessedAt = DateTimeOffset.UtcNow;
                    await _tradingDbContext.SaveChangesAsync();
                },
                $"{nameof(MarkOutboxMessageAsProcessedAsync)}:Save:{clientOrderId}");
            }
        }

        public async Task<bool> DeleteDeadLetterLogAsync(Guid id)
        {
            var deadLetterLog = await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.DeadLetterLogs
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == id),
                $"{nameof(DeleteDeadLetterLogAsync)}:Fetch:{id}");

            if (deadLetterLog == null)
            {
                return false;
            }

            await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
            {
                _tradingDbContext.DeadLetterLogs.Remove(deadLetterLog);
                await _tradingDbContext.SaveChangesAsync();
            },
            $"{nameof(DeleteDeadLetterLogAsync)}:Delete:{id}");

            return true;
        }

        public async Task<int> DeleteAllDeadLetterLogsAsync()
        {
            var deadLetterLogs = await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                 await _tradingDbContext.DeadLetterLogs.AsNoTracking().ToListAsync(),
                 nameof(DeleteAllDeadLetterLogsAsync) + ":Fetch");

            var count = deadLetterLogs.ToList().Count;

            if (count > 0)
            {
                await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                {
                    _tradingDbContext.DeadLetterLogs.RemoveRange(deadLetterLogs);
                    await _tradingDbContext.SaveChangesAsync();
                },
                nameof(DeleteAllDeadLetterLogsAsync) + ":Delete");
            }

            return count;
        }
    }
}
