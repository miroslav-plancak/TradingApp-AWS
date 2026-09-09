using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services.Helpers;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.ConversationChunk;

namespace TradingApp.Business.Repositories
{
    public class ConversationChunkRepository : IConversationChunkRepository
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IResilienceConversationPolicyGuard _resiliencePolicyGuard;

        public ConversationChunkRepository
        (
            TradingDbContext tradingDbContext, 
            IResilienceConversationPolicyGuard resiliencePolicyGuard 
        )
        {
            _tradingDbContext = tradingDbContext;
            _resiliencePolicyGuard = resiliencePolicyGuard;
        }

        public async Task<ConversationChunk> CreateConversationChunkAsync(ConversationChunk conversationChunk)
        {
            return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
            {
                var existingRow = await _tradingDbContext.ConversationChunks
                        .AsNoTracking()
                        .SingleOrDefaultAsync(x => 
                            x.ConversationId == conversationChunk.ConversationId && 
                            x.Key == conversationChunk.Key
                        );

                if(existingRow != null)
                {
                    return existingRow;
                }

                conversationChunk.Id = Guid.NewGuid();
                conversationChunk.CreatedAt = DateTimeOffset.UtcNow;

                _tradingDbContext.ConversationChunks.Add(conversationChunk);
                await _tradingDbContext.SaveChangesAsync();

                return conversationChunk;
            }, $"{nameof(CreateConversationChunkAsync)}:Save:{conversationChunk.Id}");
        }

        public async Task<IEnumerable<ConversationChunk>> GetConversationChunksAsync(Guid conversationId)
        {
            return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
            {
                if (conversationId == Guid.Empty)
                {
                    return [];
                }

                var allConversationChunks = await _tradingDbContext.ConversationChunks
                            .AsNoTracking()
                            .Where(x => x.ConversationId == conversationId)
                            .OrderBy(x => x.CreatedAt)
                            .ToListAsync();

                return allConversationChunks;

            }, $"{nameof(GetConversationChunksAsync)}:FetchAll:{conversationId}");
        }
    }
}
