using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services.Helpers;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.ConversationFullFile;

namespace TradingApp.Business.Repositories
{

    public class ConversationFullFileRepository : IConversationFullFileRepository
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IResilienceConversationPolicyGuard _resiliencePolicyGuard;

        public ConversationFullFileRepository
        (
            TradingDbContext tradingDbContext,
            IResilienceConversationPolicyGuard resiliencePolicyGuard
        )
        {
            _tradingDbContext = tradingDbContext;
            _resiliencePolicyGuard = resiliencePolicyGuard;
        }

        public async Task<ConversationFullFile> CreateConversationFullFileAsync(ConversationFullFile conversationFullFile)
        {
            return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
            {
                var existingRow = await _tradingDbContext.ConversationFullFiles
                        .AsNoTracking()
                        .SingleOrDefaultAsync(x =>
                            x.ConversationId == conversationFullFile.ConversationId &&
                            x.SourceFile == conversationFullFile.SourceFile
                        );

                if (existingRow != null)
                {
                    return existingRow;
                }

                conversationFullFile.Id = Guid.NewGuid();
                conversationFullFile.CreatedAt = DateTimeOffset.UtcNow;

                _tradingDbContext.ConversationFullFiles.Add(conversationFullFile);
                await _tradingDbContext.SaveChangesAsync();

                return conversationFullFile;
            }, $"{nameof(CreateConversationFullFileAsync)}:Save:{conversationFullFile.Id}");
        }

        public async Task<IEnumerable<ConversationFullFile>> GetConversationFullFilesAsync(Guid? conversationId)
        {
            return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
            {
                if (conversationId == Guid.Empty)
                {
                    return [];
                }

                var allConversationFullFiles = await _tradingDbContext.ConversationFullFiles
                            .AsNoTracking()
                            .Where(x => x.ConversationId == conversationId)
                            .OrderBy(x => x.CreatedAt)
                            .ToListAsync();

                return allConversationFullFiles;

            }, $"{nameof(GetConversationFullFilesAsync)}:FetchAll:{conversationId}");
        }
    }
}
