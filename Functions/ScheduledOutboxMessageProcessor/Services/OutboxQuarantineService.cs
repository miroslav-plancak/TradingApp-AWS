using Amazon.Lambda.Core;
using Handler.Interfaces;
using Microsoft.EntityFrameworkCore;
using TradingApp.Domain;
using TradingApp.Domain.Models.Entities.OutboxMessage;
using TradingApp.Domain.Models.Entities.QuarantinedOutboxMessage;

namespace Handler.Services
{
    public class OutboxQuarantineService : IOutboxQuarantineService
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IDbContextFactory<TradingDbContext> _dbContextFactory;

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

            using var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);

            var tasks = exhaustedOutboxMessages.Select(async exObMsg =>
            {
                await semaphore.WaitAsync();

                try
                {
                    await QuarantineExhaustedMessageAsync(exObMsg, context);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }

        private async Task QuarantineExhaustedMessageAsync(OutboxMessage exObMsg, ILambdaContext context)
        {
            TradingDbContext exObMsgDbContext;

            try
            {
                exObMsgDbContext = await _dbContextFactory.CreateDbContextAsync();
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"DbContextCreationFailed | Error: {ex.Message}");
                return;
            }

            await using (exObMsgDbContext)
            {
                try
                {
                    Guid? clientOrderId = Guid.TryParse(exObMsg.Payload, out var parsed) ? parsed : null;

                    exObMsgDbContext.QuarantinedOutboxMessages.Add(new QuarantinedOutboxMessage
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
                    exObMsgDbContext.OutboxMessages.Attach(outboxStub);
                    exObMsgDbContext.Entry(outboxStub).Property(x => x.ProcessedAt).IsModified = true;
                    outboxStub.ProcessedAt = DateTimeOffset.UtcNow;

                    await exObMsgDbContext.SaveChangesAsync();

                    context.Logger.LogWarning(
                        $"QuarantiningMessage | CorrelationId: {exObMsg.CorrelationId} | OutboxId: {exObMsg.Id} | Reason: {exObMsg.RetryReason} | RetryCount: {exObMsg.RetryCount}");
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(
                        $"QuarantineWriteFailed | CorrelationId: {exObMsg.CorrelationId} | OutboxId: {exObMsg.Id} | Error: {ex.Message}");
                }
            }
        }
    }
}
