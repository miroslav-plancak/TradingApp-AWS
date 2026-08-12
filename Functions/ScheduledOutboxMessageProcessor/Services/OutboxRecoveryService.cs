using Amazon.Lambda.Core;
using Handler.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.OutboxMessage;
using TradingApp.Domain.Models.Entities.QuarantinedOutboxMessage;
using TradingApp.Domain.Models.Enums;
using TradingApp.Infrastructure;

namespace Handler.Services
{
    public class OutboxRecoveryService : IOutboxRecoveryService
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IDbContextFactory<TradingDbContext> _dbContextFactory;
        private readonly IAsyncPolicy _sqlResiliencePolicy;

        public OutboxRecoveryService(
            TradingDbContext tradingDbContext,
            IDbContextFactory<TradingDbContext> dbContextFactory,
            [FromKeyedServices(ResiliencePolicyKey.Sql)] IAsyncPolicy sqlResiliencePolicy
        )
        {
            _tradingDbContext = tradingDbContext;
            _dbContextFactory = dbContextFactory;
            _sqlResiliencePolicy = sqlResiliencePolicy;
        }

        public async Task AutoRecoverResurrectedMessagesAsync(ILambdaContext context, int maxDegreeOfParallelism)
        {
            var resurrectCandidates = await _sqlResiliencePolicy.ExecuteAsync(async () =>
                await _tradingDbContext.QuarantinedOutboxMessages
                  .AsNoTracking()
                  .Where(q => !q.IsResurrected
                           && !q.IsDiscarded
                           && q.Reason == OutboxRetryReason.SimpleQueueServiceUnavailable)
                  .ToListAsync());

            if (resurrectCandidates.Count == 0)
            {
                context.Logger.LogWarning($"AutoRecoveryPhaseSkipped | no resurrection candidates found.");
                return;
            }

            context.Logger.LogWarning($"AutoRecoveryPhase | Found {resurrectCandidates.Count} resurrection candidates");

            var originalMessageIds = resurrectCandidates
                .Select(c => c.OriginalOutboxMessageId)
                .ToHashSet();

            var existingOriginalMessageIds = await _sqlResiliencePolicy.ExecuteAsync(async () =>
                await _tradingDbContext.OutboxMessages
                    .Where(x => originalMessageIds.Contains(x.Id))
                    .Select(x => x.Id)
                    .ToHashSetAsync());

            using var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);

            var tasks = resurrectCandidates.Select(async candidate =>
            {
                if (!existingOriginalMessageIds.Contains(candidate.OriginalOutboxMessageId))
                {
                    await MarkCandidateDiscardedAsync(candidate, context);
                    return;
                }

                await semaphore.WaitAsync();

                try
                {
                    await AutoRecoverResurrectedMessageAsync(candidate, context);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            context.Logger.LogWarning($"AutoRecoveryComplete | Resurrected {resurrectCandidates.Count} messages");
        }

        private async Task AutoRecoverResurrectedMessageAsync(QuarantinedOutboxMessage candidate, ILambdaContext context)
        {
            TradingDbContext candidateDbContext;

            try
            {
                candidateDbContext = await _dbContextFactory.CreateDbContextAsync();
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"DbContextCreationFailed | CorrelationId: {candidate.CorrelationId} | Error: {ex.Message}");
                return;
            }

            await using (candidateDbContext)
            {
                try
                {
                    var outboxStub = new OutboxMessage { Id = candidate.OriginalOutboxMessageId };
                    candidateDbContext.OutboxMessages.Attach(outboxStub);
                    candidateDbContext.Entry(outboxStub).Property(x => x.ProcessedAt).IsModified = true;
                    candidateDbContext.Entry(outboxStub).Property(x => x.RetryCount).IsModified = true;
                    candidateDbContext.Entry(outboxStub).Property(x => x.RetryReason).IsModified = true;
                    outboxStub.ProcessedAt = null;
                    outboxStub.RetryCount = 4;
                    outboxStub.RetryReason = OutboxRetryReason.None;

                    var quarantineStub = new QuarantinedOutboxMessage { Id = candidate.Id };
                    candidateDbContext.QuarantinedOutboxMessages.Attach(quarantineStub);
                    candidateDbContext.Entry(quarantineStub).Property(x => x.IsResurrected).IsModified = true;
                    candidateDbContext.Entry(quarantineStub).Property(x => x.ResurrectedAt).IsModified = true;
                    candidateDbContext.Entry(quarantineStub).Property(x => x.ResolutionNotes).IsModified = true;
                    quarantineStub.IsResurrected = true;
                    quarantineStub.ResurrectedAt = DateTimeOffset.UtcNow;
                    quarantineStub.ResolutionNotes = "Auto-resurrected: Queue connectivity restored";

                    await _sqlResiliencePolicy.ExecuteAsync(async () => await candidateDbContext.SaveChangesAsync());

                    context.Logger.LogWarning(
                        $"ResurrectingMessage | CorrelationId: {candidate.CorrelationId} | OutboxId: {candidate.OriginalOutboxMessageId} | QuarantinedId: {candidate.Id}");
                }
                catch (BrokenCircuitException)
                {
                    context.Logger.LogWarning(
                        $"CircuitOpen | Database unreachable | CorrelationId: {candidate.CorrelationId} | QuarantinedId: {candidate.Id} | Will retry next cycle");
                }
                catch (DbUpdateConcurrencyException)
                {
                    await MarkCandidateDiscardedAsync(candidate, context);
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(
                        $"AutoRecoveryWriteFailed | CorrelationId: {candidate.CorrelationId} | QuarantinedId: {candidate.Id} | Error: {ex.Message}");
                }
            }
        }

        private async Task MarkCandidateDiscardedAsync(QuarantinedOutboxMessage candidate, ILambdaContext context)
        {
            TradingDbContext candidateDbContext;

            try
            {
                candidateDbContext = await _dbContextFactory.CreateDbContextAsync();
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"DbContextCreationFailed | CorrelationId: {candidate.CorrelationId} | Error: {ex.Message}");
                return;
            }

            await using (candidateDbContext)
            {
                context.Logger.LogWarning(
                    $"CandidateDiscardStarted | CorrelationId: {candidate.CorrelationId} | OutboxId: {candidate.OriginalOutboxMessageId} | QuarantinedId: {candidate.Id}");
                try
                {
                    var quarantineStub = new QuarantinedOutboxMessage { Id = candidate.Id };
                    candidateDbContext.QuarantinedOutboxMessages.Attach(quarantineStub);
                    candidateDbContext.Entry(quarantineStub).Property(x => x.IsDiscarded).IsModified = true;
                    candidateDbContext.Entry(quarantineStub).Property(x => x.DiscardedAt).IsModified = true;
                    candidateDbContext.Entry(quarantineStub).Property(x => x.DiscardedBy).IsModified = true;
                    quarantineStub.IsDiscarded = true;
                    quarantineStub.DiscardedAt = DateTimeOffset.UtcNow;
                    quarantineStub.DiscardedBy = "TradingApp-AWS admin";

                    await _sqlResiliencePolicy.ExecuteAsync(async () => await candidateDbContext.SaveChangesAsync());

                    context.Logger.LogWarning(
                        $"CandidateDiscarded | CorrelationId: {candidate.CorrelationId} | OutboxId: {candidate.OriginalOutboxMessageId} | QuarantinedId: {candidate.Id}");
                }
                catch (BrokenCircuitException)
                {
                    context.Logger.LogWarning(
                        $"CircuitOpen | Database unreachable | CorrelationId: {candidate.CorrelationId} | QuarantinedId: {candidate.Id} | Will retry next cycle");
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(
                        $"CandidateDiscardFailed | CorrelationId: {candidate.CorrelationId} | QuarantinedId: {candidate.Id} | Error: {ex.Message}");
                }
            }
        }
    }
}
