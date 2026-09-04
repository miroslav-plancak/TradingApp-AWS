using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Outbox;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services.Helpers;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.OutboxMessage;

namespace TradingApp.Business.Repositories
{
    public class OutboxMessageRepository : IOutboxMessageRepository
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IResiliencePolicyGuard _resiliencePolicyGuard;

        public OutboxMessageRepository(TradingDbContext tradingDbContext, IResiliencePolicyGuard resiliencePolicyGuard)
        {
            _tradingDbContext = tradingDbContext;
            _resiliencePolicyGuard = resiliencePolicyGuard;
        }

        public async Task<OutboxMessage> GetByIdAsync(Guid id)
        {
            return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.OutboxMessages
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == id),
                $"{nameof(GetByIdAsync)}:Fetch:{id}");
        }

        public async Task<OutboxMessage> GetByClientOrderIdAsync(Guid clientOrderId)
        {
            return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.OutboxMessages
                    .AsNoTracking()
                    .Where(x => x.Payload == clientOrderId.ToString())
                    .OrderByDescending(x => x.CreatedAt)
                    .FirstOrDefaultAsync(),
                $"{nameof(GetByClientOrderIdAsync)}:Fetch:{clientOrderId}");
        }

        public async Task<IEnumerable<OutboxMessage>> GetAllAsync()
        {
            return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.OutboxMessages
                    .AsNoTracking()
                    .OrderByDescending(x => x.CreatedAt)
                    .ToListAsync(),
                nameof(GetAllAsync) + ":Fetch");
        }

        public async Task<IEnumerable<OutboxMessage>> GetUnprocessedAsync()
        {
            return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.OutboxMessages
                    .AsNoTracking()
                    .Where(x => x.ProcessedAt == null)
                    .OrderByDescending(x => x.CreatedAt)
                    .ToListAsync(),
                nameof(GetUnprocessedAsync) + ":Fetch");
        }

        public async Task<IEnumerable<OutboxMessage>> GetProcessedAsync()
        {
            return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.OutboxMessages
                    .AsNoTracking()
                    .Where(x => x.ProcessedAt != null)
                    .OrderByDescending(x => x.CreatedAt)
                    .ToListAsync(),
                nameof(GetProcessedAsync) + ":Fetch");
        }

        public async Task<OutboxMessage> MarkAsProcessedAsync(Guid id)
        {
            var outboxMessage = await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.OutboxMessages
                    .SingleOrDefaultAsync(x => x.Id == id),
                $"{nameof(MarkAsProcessedAsync)}:Fetch:{id}");

            if (outboxMessage == null)
            {
                return null;
            }

            await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
            {
                outboxMessage.ProcessedAt = DateTimeOffset.UtcNow;
                await _tradingDbContext.SaveChangesAsync();
            },
            $"{nameof(MarkAsProcessedAsync)}:Save:{id}");

            return outboxMessage;
        }

        public async Task<bool> DeleteAsync(Guid id)
        {
            var outboxMessage = await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.OutboxMessages
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == id),
                $"{nameof(DeleteAsync)}:Fetch:{id}");

            if (outboxMessage == null)
            {
                return false;
            }

            await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
            {
                _tradingDbContext.OutboxMessages.Remove(outboxMessage);
                await _tradingDbContext.SaveChangesAsync();
            },
            $"{nameof(DeleteAsync)}:Delete:{id}");

            return true;
        }

        public async Task<int> DeleteAllAsync()
        {
            var outboxMessages = await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.OutboxMessages.AsNoTracking().ToListAsync(),
                nameof(DeleteAllAsync) + ":Fetch");

            var count = outboxMessages.Count;

            if (count > 0)
            {
                await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                {
                    _tradingDbContext.OutboxMessages.RemoveRange(outboxMessages);
                    await _tradingDbContext.SaveChangesAsync();
                },
                nameof(DeleteAllAsync) + ":Delete");
            }

            return count;
        }

        public async Task<OutboxMessageStatsDTO> GetStatsAsync()
        {
            var allMessages = await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.OutboxMessages
                    .AsNoTracking()
                    .ToListAsync(),
                nameof(GetStatsAsync) + ":Fetch");

            var stats = new OutboxMessageStatsDTO
            {
                TotalCount = allMessages.Count,
                ProcessedCount = allMessages.Count(x => x.ProcessedAt.HasValue),
                UnprocessedCount = allMessages.Count(x => !x.ProcessedAt.HasValue),
                Last24Hours = allMessages.Count(x => x.CreatedAt >= DateTimeOffset.UtcNow.AddHours(-24))
            };

            return stats;
        }
    }
}
