using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;

namespace TradingApp.Infrastructure
{
    public static class SqsBatchHandler
    {
        // We return SQSBatchResponse with a list of BatchItemFailures to ensure that if there are
        // failed processed messages in the current batch, we return ONLY those so that they can be re-delivered
        // by the SQS. This way successfully processed messages are processed only once and do not risk
        // being re-delivered if a single/multiple messages in a batch fail. Without this mechanism a single
        // failed message in a batch would mean that the SQS would re-deliver all of the messages in that
        // batch, regardless of their failed/succeeded status, potentially leading to duplicate processing.
        public static async Task<SQSBatchResponse> BatchSqsMessages
        (
            SQSEvent evnt,
            ILambdaContext context,
            Func<SQSEvent.SQSMessage, ILambdaContext, Task> handler
        )
        {
            var batchItemFailures = new List<SQSBatchResponse.BatchItemFailure>();

            foreach (var record in evnt.Records)
            {
                try
                {
                    await handler(record, context);
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(
                        $"RecordProcessingFailed | MessageId: {record.MessageId} | Error: {ex.Message}");

                    batchItemFailures.Add(new SQSBatchResponse.BatchItemFailure
                    {
                        ItemIdentifier = record.MessageId
                    });
                }
            }

            return new SQSBatchResponse { BatchItemFailures = batchItemFailures };
        }
    }
}
