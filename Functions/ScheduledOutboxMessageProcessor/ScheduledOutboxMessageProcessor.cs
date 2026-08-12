using Amazon.Lambda.Annotations;
using Amazon.Lambda.Core;
using Amazon.SQS;
using Handler.Interfaces;
using Handler.Settings;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Handler
{
    public class ScheduledOutboxMessageProcessor
    {
        private readonly string _createOrderQueueUrl;
        private readonly IAmazonSQS _sqsClient;

        private readonly IOutboxQuarantineService _outboxQuarantineService;
        private readonly IOutboxProcessingService _outboxProcessingService;
        private readonly IOutboxRecoveryService _outboxRecoveryService;

        private const int MaxDegreeOfParallelism = 5;

        public ScheduledOutboxMessageProcessor(
            IAmazonSQS sqsClient,
            IOutboxQuarantineService outboxQuarantineService,
            IOutboxProcessingService outboxProcessingService,
            IOutboxRecoveryService outboxRecoveryService,
            OutboxMessageProcessorSettings settings
        )
        {
            _sqsClient = sqsClient;
            _outboxQuarantineService = outboxQuarantineService;
            _outboxProcessingService = outboxProcessingService;
            _outboxRecoveryService = outboxRecoveryService;
            _createOrderQueueUrl = settings.CreateOrderQueueUrl;
        }

        [LambdaFunction(Timeout = 120)]
        public async Task FunctionHandler(ILambdaContext context)
        {
            context.Logger.LogWarning($"ScheduledOutboxMessageProcessor triggered at: {DateTimeOffset.UtcNow}");

            await _outboxQuarantineService.QuarantineExhaustedMessagesAsync(context, MaxDegreeOfParallelism);

            var isQueueReachable = await IsQueueReachableAsync(context);

            if (isQueueReachable)
            {
                await _outboxProcessingService.ProcessOutboxMessagesConcurrentlyAsync(context, MaxDegreeOfParallelism);
                await _outboxRecoveryService.AutoRecoverResurrectedMessagesAsync(context, MaxDegreeOfParallelism);
            }
            else
            {
                context.Logger.LogWarning(
                    "QueueDown | Skipping ProcessOutboxMessagesConcurrentlyAsync() and AutoRecoverResurrectedMessagesAsync() this cycle.");
            }
        }

        private async Task<bool> IsQueueReachableAsync(ILambdaContext context)
        {
            try
            {
                await _sqsClient.GetQueueAttributesAsync(_createOrderQueueUrl, new List<string> { "QueueArn" });

                context.Logger.LogWarning("QueueReachable | CREATE_ORDER_QUEUE.fifo is accessible.");
                return true;
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning($"QueueUnreachable | Cannot connect to queue | Error: {ex.Message}");
                return false;
            }
        }

    }
}
