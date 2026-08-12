using Amazon.Lambda.Core;

namespace Handler.Interfaces
{
    public interface IOutboxQuarantineService
    {
        Task QuarantineExhaustedMessagesAsync(ILambdaContext context, int MaxDegreeOfParallelism);
    }
}
