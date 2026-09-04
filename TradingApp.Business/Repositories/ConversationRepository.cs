using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services.Helpers;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.Conversation;

namespace TradingApp.Business.Repositories
{
    public class ConversationRepository : IConversationRepository
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IResilienceConversationPolicyGuard _resiliencePolicyGuard;
        public ConversationRepository(TradingDbContext tradingDbContext, IResilienceConversationPolicyGuard resiliencePolicyGuard)
        {
            _tradingDbContext = tradingDbContext;
            _resiliencePolicyGuard = resiliencePolicyGuard;
        }

        public async Task<Conversation> CreateConversationAsync(Conversation conversation, Guid? clientRequestId)
        {
            return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
            {
                if (clientRequestId is not null)
                {
                    var existingRow = await _tradingDbContext.Conversations
                        .AsNoTracking()
                        .SingleOrDefaultAsync(x => x.ClientRequestId == clientRequestId);
                    if (existingRow is not null)
                    {
                        return existingRow;
                    }
                }

                conversation.Id = Guid.NewGuid();
                conversation.ClientRequestId = clientRequestId;
                conversation.CreatedAt = DateTimeOffset.UtcNow;
                conversation.UpdatedAt = DateTimeOffset.UtcNow;

                _tradingDbContext.Conversations.Add(conversation);
                await _tradingDbContext.SaveChangesAsync();

                return conversation;
            }, $"{nameof(CreateConversationAsync)}:Save:{conversation.Id}");
        }

        public async Task<Conversation> GetConversationById(Guid conversationId)
        {
            return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                await _tradingDbContext.Conversations
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == conversationId),
                $"{nameof(GetConversationById)}:Fetch:{conversationId}");
        }
    }
}
