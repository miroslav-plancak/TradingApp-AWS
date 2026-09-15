namespace TradingApp.Infrastructure.Exceptions
{
    public class ChatStreamFailureException : Exception
    {
        public bool IsRetryable { get; }

        public ChatStreamFailureException(string failureMessage, bool isRetryable) : base(failureMessage)
        {
            IsRetryable = isRetryable;
        }
    }
}
