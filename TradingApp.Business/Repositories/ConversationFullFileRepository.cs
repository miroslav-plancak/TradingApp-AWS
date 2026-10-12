using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services.Helpers;
using TradingApp.Business.Services.Helpers;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.ConversationFullFile;

namespace TradingApp.Business.Repositories
{

    public class ConversationFullFileRepository : IConversationFullFileRepository
    {
        private readonly IDbContextFactory<TradingDbContext> _dbContextFactory;
        private readonly IResilienceConversationPolicyGuard _resiliencePolicyGuard;

        public ConversationFullFileRepository
        (
            IDbContextFactory<TradingDbContext> dbContextFactory,
            IResilienceConversationPolicyGuard resiliencePolicyGuard
        )
        {
            _dbContextFactory = dbContextFactory;
            _resiliencePolicyGuard = resiliencePolicyGuard;
        }

        public async Task<ConversationFullFile> CreateConversationFullFileAsync(ConversationFullFile conversationFullFile)
        {
            TradingDbContext tradingDbContextFactory = await _dbContextFactory.CreateDbContextAsync();

            await using (tradingDbContextFactory)
            {

                return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                {
                    var existingRow = await tradingDbContextFactory.ConversationFullFiles
                            .AsNoTracking()
                            .SingleOrDefaultAsync(x =>
                                x.ConversationId == conversationFullFile.ConversationId &&
                                x.SourceFile == conversationFullFile.SourceFile
                            );

                    if (existingRow != null)
                    {
                        conversationFullFile.Id = existingRow.Id;
                        conversationFullFile.CreatedAt = existingRow.CreatedAt;

                        tradingDbContextFactory.ConversationFullFiles.Update(conversationFullFile);
                        await tradingDbContextFactory.SaveChangesAsync();

                        return conversationFullFile;
                    }

                    conversationFullFile.Id = Guid.NewGuid();
                    conversationFullFile.CreatedAt = DateTimeOffset.UtcNow;

                    tradingDbContextFactory.ConversationFullFiles.Add(conversationFullFile);

                    try 
                    {
                        await tradingDbContextFactory.SaveChangesAsync();
                        return conversationFullFile;

                    } 
                    catch(DbUpdateException ex) when (SqlExceptionClassifier.IsUniqueConstraintOrUniqueIndexViolation(ex))
                    {
                        tradingDbContextFactory.Entry(conversationFullFile).State = EntityState.Detached;

                        var raceWinnerRow = await tradingDbContextFactory.ConversationFullFiles
                         .AsNoTracking()
                         .SingleOrDefaultAsync(x =>
                             x.ConversationId == conversationFullFile.ConversationId &&
                             x.SourceFile == conversationFullFile.SourceFile);

                        conversationFullFile.Id = raceWinnerRow.Id;
                        conversationFullFile.CreatedAt = raceWinnerRow.CreatedAt;

                        tradingDbContextFactory.ConversationFullFiles.Update(conversationFullFile);
                        await tradingDbContextFactory.SaveChangesAsync();

                        return conversationFullFile;
                    }
               
                }, $"{nameof(CreateConversationFullFileAsync)}:Save:{conversationFullFile.Id}");
            }
        }

        public async Task<IEnumerable<ConversationFullFile>> GetConversationFullFilesAsync(Guid? conversationId)
        {
            TradingDbContext tradingDbContextFactory = await _dbContextFactory.CreateDbContextAsync();

            await using (tradingDbContextFactory)
            {
                return await _resiliencePolicyGuard.GuardViaResiliencePolicyAsync(async () =>
                {
                    if (conversationId == Guid.Empty)
                    {
                        return [];
                    }

                    var allConversationFullFiles = await tradingDbContextFactory.ConversationFullFiles
                                .AsNoTracking()
                                .Where(x => x.ConversationId == conversationId)
                                .OrderBy(x => x.CreatedAt)
                                .ToListAsync();

                    return allConversationFullFiles;

                }, $"{nameof(GetConversationFullFilesAsync)}:FetchAll:{conversationId}");
            }
        }
    }
}
