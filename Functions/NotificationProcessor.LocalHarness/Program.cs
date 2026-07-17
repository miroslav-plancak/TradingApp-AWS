using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.SQS;
using Amazon.SQS.Model;

var sqsClient = new AmazonSQSClient(Amazon.RegionEndpoint.EUNorth1);
var queueUrl = "https://sqs.eu-north-1.amazonaws.com/465861110788/notification_queue.fifo";
var function = new NotificationProcessor.NotificationProcessor();

Console.WriteLine("Listening for order status events on notification_queue.fifo... (Ctrl+C to stop)");

while (true)
{
    var response = await sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
    {
        QueueUrl = queueUrl,
        MaxNumberOfMessages = 10,
        WaitTimeSeconds = 20,
        MessageAttributeNames = new List<string> { "All" },
        MessageSystemAttributeNames = new List<string> { "All" }
    });

    foreach (var message in response.Messages ?? new List<Message>())
    {
        try
        {
            var sqsEvent = new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>
                  {
                      new SQSEvent.SQSMessage
                      {
                          MessageId = message.MessageId,
                          Body = message.Body,
                          ReceiptHandle = message.ReceiptHandle,
                          Attributes = message.Attributes,
                          MessageAttributes = message.MessageAttributes?.ToDictionary(
                              kvp => kvp.Key, kvp => new SQSEvent.MessageAttribute
                              {
                                  DataType = kvp.Value.DataType,
                                  StringValue = kvp.Value.StringValue
                              })
                      }
                  }
            };

            await function.FunctionHandler(sqsEvent, new TestLambdaContext());
            await sqsClient.DeleteMessageAsync(queueUrl, message.ReceiptHandle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Message {message.MessageId} failed: {ex.Message}");
            // deliberately not deleted - stays on the queue, redelivered after the visibility timeout
        }
    }
}
