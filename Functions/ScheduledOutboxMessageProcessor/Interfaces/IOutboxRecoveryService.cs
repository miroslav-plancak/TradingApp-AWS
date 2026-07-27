using Amazon.Lambda.Core;

namespace Handler.Interfaces
{
    public interface IOutboxRecoveryService
    {
        Task AutoRecoverResurrectedMessagesAsync(ILambdaContext context, int maxDegreeOfParallelism);
    }
}
