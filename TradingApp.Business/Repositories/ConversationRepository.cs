using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.ConversationMessage;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services.Helpers;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.Conversation;
using TradingApp.Domain.Models.Entities.ConversationMessage;

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
                if (clientRequestId != null)
                {
                    var existingRow = await _tradingDbContext.Conversations
                        .AsNoTracking()
                        .SingleOrDefaultAsync(x => x.ClientRequestId == clientRequestId);
                    if (existingRow != null)
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

        public async Task<bool> DeleteConversationByIdAsync(Guid conversationId)
        {
            var conversation = await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>

                await _tradingDbContext.Conversations
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == conversationId),
                    $"{nameof(DeleteConversationByIdAsync)}:Fetch:{conversationId}");

            if (conversation == null) return false;

            await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
            {
                _tradingDbContext.Conversations.Remove(conversation);
                await _tradingDbContext.SaveChangesAsync();
            },
            $"{nameof(DeleteConversationByIdAsync)}:Delete:{conversationId}");

            return true;
        }

        public async Task<ConversationMessage> CreateConversationMessageAsync(CreateConversationMessageRequestDTO request)
        {
            return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
            {
                if(request.ClientRequestId != null)
                {
                    var existingConversationMessage = await _tradingDbContext.ConversationMessages
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ConversationId == request.ConversationId && x.ClientRequestId == request.ClientRequestId);

                    if (existingConversationMessage != null)
                    {
                        return existingConversationMessage;
                    }
                }

                var newConversationMessage = new ConversationMessage
                {
                    Id = Guid.NewGuid(),
                    ConversationId = request.ConversationId,
                    ClientRequestId = request.ClientRequestId,
                    Role = request.Role,
                    Body = request.Body,
                    CreatedAt = DateTimeOffset.UtcNow
                };

               _tradingDbContext.ConversationMessages.Add(newConversationMessage);
               await _tradingDbContext.SaveChangesAsync();

               return  newConversationMessage;
            },$"{nameof(CreateConversationMessageAsync)}:Create:{request.ConversationId}");
        }
    }
}
