using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services.Helpers;
using TradingApp.Business.Services.Helpers;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.ConversationChunk;

namespace TradingApp.Business.Repositories
{
    public class ConversationChunkRepository : IConversationChunkRepository
    {
        private readonly IDbContextFactory<TradingDbContext> _dbContextFactory;
        private readonly IResilienceConversationPolicyGuard _resiliencePolicyGuard;

        public ConversationChunkRepository
        (
            IResilienceConversationPolicyGuard resiliencePolicyGuard,
            IDbContextFactory<TradingDbContext> dbContextFactory
        )
        {
            _resiliencePolicyGuard = resiliencePolicyGuard;
            _dbContextFactory = dbContextFactory;
        }

        public async Task<ConversationChunk> CreateConversationChunkAsync(ConversationChunk conversationChunk)
        {
            TradingDbContext tradingDbContextFactory = await _dbContextFactory.CreateDbContextAsync();

            await using (tradingDbContextFactory)
            {
                return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async (policyCancellationToken) =>
                {
                    var existingRow = await tradingDbContextFactory.ConversationChunks
                            .AsNoTracking()
                            .SingleOrDefaultAsync(x =>
                                x.ConversationId == conversationChunk.ConversationId &&
                                x.Key == conversationChunk.Key
                            );

                    if (existingRow != null)
                    {
                        conversationChunk.Id = existingRow.Id;
                        conversationChunk.CreatedAt = existingRow.CreatedAt;

                        tradingDbContextFactory.ConversationChunks.Update(conversationChunk);
                        await tradingDbContextFactory.SaveChangesAsync();

                        return conversationChunk;
                    }

                    conversationChunk.Id = Guid.NewGuid();
                    conversationChunk.CreatedAt = DateTimeOffset.UtcNow;

                    tradingDbContextFactory.ConversationChunks.Add(conversationChunk);

                    try
                    {
                        await tradingDbContextFactory.SaveChangesAsync();
                        return conversationChunk;
                    }
                    catch (DbUpdateException ex) when (SqlExceptionClassifier.IsUniqueConstraintOrUniqueIndexViolation(ex))
                    {
                        tradingDbContextFactory.Entry(conversationChunk).State = EntityState.Detached;

                        var raceWinnerRow = await tradingDbContextFactory.ConversationChunks
                            .AsNoTracking()
                            .SingleOrDefaultAsync(x =>
                                x.ConversationId == conversationChunk!.ConversationId &&
                                x.Key == conversationChunk.Key
                            );

                        conversationChunk.Id = raceWinnerRow.Id;
                        conversationChunk.CreatedAt = raceWinnerRow.CreatedAt;

                        tradingDbContextFactory.ConversationChunks.Update(conversationChunk);
                        await tradingDbContextFactory.SaveChangesAsync();

                        return conversationChunk;
                    }

                }, $"{nameof(CreateConversationChunkAsync)}:Save:{conversationChunk.Id}");
            }
        }

        public async Task<IEnumerable<ConversationChunk>> GetConversationChunksAsync(Guid conversationId, CancellationToken ct)
        {
            TradingDbContext tradingDbContextFactory = await _dbContextFactory.CreateDbContextAsync(ct);

            ct.ThrowIfCancellationRequested();

            await using (tradingDbContextFactory)
            {
                return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async (policyCancellationToken) =>
                {
                    if (conversationId == Guid.Empty)
                    {
                        return [];
                    }

                    var allConversationChunks = await tradingDbContextFactory.ConversationChunks
                                .AsNoTracking()
                                .Where(x => x.ConversationId == conversationId)
                                .OrderBy(x => x.CreatedAt)
                                .ToListAsync(policyCancellationToken);

                    return allConversationChunks;

                }, $"{nameof(GetConversationChunksAsync)}:FetchAll:{conversationId}", ct);
            }
        }

        public async Task<int> DeleteConversationChunksByConversationIdAsync(Guid conversationId)
        {
            TradingDbContext tradingDbContextFactory = await _dbContextFactory.CreateDbContextAsync();

            await using (tradingDbContextFactory)
            {
                return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async (policyCancellationToken) =>
                    await tradingDbContextFactory.ConversationChunks
                        .Where(x => x.ConversationId == conversationId)
                        .ExecuteDeleteAsync(policyCancellationToken),
                    $"{nameof(DeleteConversationChunksByConversationIdAsync)}:Delete:{conversationId}");
            }
        }
    }
}
