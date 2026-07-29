using Amazon.Lambda.Core;
using Handler.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Threading.Channels;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.OutboxMessage;
using TradingApp.Domain.Models.Entities.QuarantinedOutboxMessage;

namespace Handler.Services
{
    public class OutboxQuarantineService : IOutboxQuarantineService
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IDbContextFactory<TradingDbContext> _dbContextFactory;

        private const bool UseWorkerPoolProcessing = false;

        public OutboxQuarantineService(TradingDbContext tradingDbContext, IDbContextFactory<TradingDbContext> dbContextFactory)
        {
            _tradingDbContext = tradingDbContext;
            _dbContextFactory = dbContextFactory;
        }

        public async Task QuarantineExhaustedMessagesAsync(ILambdaContext context, int maxDegreeOfParallelism)
        {
            var exhaustedOutboxMessages = await _tradingDbContext.OutboxMessages
                  .Where(x => x.ProcessedAt == null && x.RetryCount >= 5)
                  .OrderBy(x => x.CreatedAt)
                  .Take(50)
                  .ToListAsync();

            if (exhaustedOutboxMessages.Count == 0)
            {
                context.Logger.LogWarning($"QuarantinePhaseSkipped | no exhausted messages found.");
                return;
            }

            context.Logger.LogWarning($"QuarantinePhase | Found {exhaustedOutboxMessages.Count} exhausted messages");

            await (UseWorkerPoolProcessing
                 ? QuarantineExhaustedMessagesViaWorkerPoolAsync(context, maxDegreeOfParallelism, exhaustedOutboxMessages)
                 : QuarantineExhaustedMessagesViaSemaphorePoolAsync(context, maxDegreeOfParallelism, exhaustedOutboxMessages));
        }

        private async Task QuarantineExhaustedMessagesViaWorkerPoolAsync(ILambdaContext context, int maxDegreeOfParallelism, List<OutboxMessage> exObMsgs)
        {
            var channel = Channel.CreateUnbounded<OutboxMessage>();

            foreach(var exObMsg in exObMsgs)
            {
                channel.Writer.TryWrite(exObMsg);
            }

            channel.Writer.Complete();

            var workers = Enumerable.Range(0, maxDegreeOfParallelism)
                .Select(_ => Task.Run(async () =>
                {
                    var dbContext = await TryCreateDbContext(context);

                    if (dbContext == null)
                        return;

                    await using (dbContext)
                    {
                        await foreach (var msg in channel.Reader.ReadAllAsync())
                        {
                            await QuarantineExhaustedMessageAsync(msg, context, dbContext);
                            dbContext.ChangeTracker.Clear();
                        }
                    }
                }));

            await Task.WhenAll(workers);
        }


        private async Task QuarantineExhaustedMessagesViaSemaphorePoolAsync(ILambdaContext context, int maxDegreeOfParallelism, List<OutboxMessage> exObMsgs)
        {
            using var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);

            var tasks = exObMsgs.Select(async exObMsg =>
            {
                await semaphore.WaitAsync();

                try
                {
                    var dbContext = await TryCreateDbContext(context);

                    if (dbContext == null) 
                        return;

                    await using (dbContext)
                    {
                        await QuarantineExhaustedMessageAsync(exObMsg, context, dbContext);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }

        private async Task QuarantineExhaustedMessageAsync(OutboxMessage exObMsg, ILambdaContext context, TradingDbContext dbContext)
        {
                try
                {
                    Guid? clientOrderId = Guid.TryParse(exObMsg.Payload, out var parsed) ? parsed : null;

                    dbContext.QuarantinedOutboxMessages.Add(new QuarantinedOutboxMessage
                    {
                        Id = Guid.NewGuid(),
                        OriginalOutboxMessageId = exObMsg.Id,
                        ClientOrderId = clientOrderId,
                        Payload = exObMsg.Payload,
                        Reason = exObMsg.RetryReason,
                        FinalRetryCount = exObMsg.RetryCount,
                        QuarantinedAt = DateTimeOffset.UtcNow,
                        ErrorMessage = exObMsg.LastError,
                        CorrelationId = exObMsg.CorrelationId
                    });

                    var outboxStub = new OutboxMessage { Id = exObMsg.Id };
                    dbContext.OutboxMessages.Attach(outboxStub);
                    dbContext.Entry(outboxStub).Property(x => x.ProcessedAt).IsModified = true;
                    outboxStub.ProcessedAt = DateTimeOffset.UtcNow;

                    await dbContext.SaveChangesAsync();
                   
                    context.Logger.LogWarning(
                        $"QuarantiningMessage | CorrelationId: {exObMsg.CorrelationId} | OutboxId: {exObMsg.Id} | Reason: {exObMsg.RetryReason} | RetryCount: {exObMsg.RetryCount}");
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(
                        $"QuarantineWriteFailed | CorrelationId: {exObMsg.CorrelationId} | OutboxId: {exObMsg.Id} | Error: {ex.Message}");
                }
        }

        private async Task<TradingDbContext?> TryCreateDbContext(ILambdaContext context)
        {
            TradingDbContext dbContext;

            try
            {
                dbContext = await _dbContextFactory.CreateDbContextAsync();
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"DbContextCreationFailed | Error: {ex.Message}");
                return null;
            }

            return dbContext;
        }
    }
}
