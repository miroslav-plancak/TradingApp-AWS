using Amazon.Lambda.TestUtilities;

Console.WriteLine("Simulating EventBridge Scheduler - running ScheduledOrderStatusProcessor every 60s... (Ctrl+C to stop)");

var function = new ScheduledOrderStatusProcessor.ScheduledOrderStatusProcessor();

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