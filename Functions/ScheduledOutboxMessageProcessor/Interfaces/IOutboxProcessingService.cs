using Amazon.Lambda.Core;

namespace Handler.Interfaces
{
    public interface IOutboxProcessingService
    {
        Task ProcessOutboxMessagesConcurrentlyAsync(ILambdaContext context, int maxDegreeOfParallelism);
    }
}
