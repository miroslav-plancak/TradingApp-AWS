namespace Handler.Settings
{
    public class OutboxMessageProcessorSettings
    {
        public string CreateOrderQueueUrl { get; }

        public OutboxMessageProcessorSettings()
        {
            CreateOrderQueueUrl = Environment.GetEnvironmentVariable("CREATE_ORDER_QUEUE_URL")
             ?? throw new InvalidOperationException("CREATE_ORDER_QUEUE_URL environment variable is not set.");
        }
    }
}
