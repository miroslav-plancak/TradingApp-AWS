using Amazon.Lambda.TestUtilities;

Console.WriteLine("Simulating EventBridge Scheduler - running ScheduledOutboxMessageProcessor every 60s... (Ctrl+C to stop)");

var function = new ScheduledOutboxMessageProcessor.ScheduledOutboxMessageProcessor();

while (true)
{
    try
    {
        await function.FunctionHandler(new TestLambdaContext());
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Tick failed: {ex.Message}");
        // deliberately not rethrown - one bad tick shouldn't kill the harness,
        // same reasoning as the ReceiveMessageAsync guard we discussed earlier
    }

    await Task.Delay(TimeSpan.FromMinutes(1));
}