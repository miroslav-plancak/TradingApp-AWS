using Amazon.Runtime;
using Anthropic.Exceptions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using StackExchange.Redis;
using System.Net;

namespace TradingApp.Infrastructure.Helpers
{
    public static class ResiliencePolicyBuilder
    {
        private static readonly HashSet<int> _sqlServerTransientErrorNumbers = new()
        {
            4060,   // Cannot open database requested by the login
            10928,  // Resource limit reached
            10929,
            40197,  // The service has encountered an error processing your request
            40501,  // The service is currently busy
            40613,  // Database unavailable
            49918,  // Cannot process request. Not enough resources
            49919,
            49920,
            1205,   // Deadlock
            233,    // Connection initialization error
            64,     // Network-related error
            -2      // Timeout
        };

        public static AsyncCircuitBreakerPolicy BuildCircuitBreakerPolicy(ILogger logger, string protectedResourceName)
        {
            return Policy
                .Handle<Exception>()
                .CircuitBreakerAsync(
                    exceptionsAllowedBeforeBreaking: 3,
                    durationOfBreak: TimeSpan.FromMinutes(2),
                    onBreak: (exception, duration) =>
                        logger.LogWarning(
                            "CircuitBreaker OPENED | {Resource} unreachable | Will retry in {Duration}s | Error: {Error}",
                            protectedResourceName, duration.TotalSeconds, exception.Message),
                    onReset: () =>
                        logger.LogWarning("CircuitBreaker CLOSED | {Resource} connectivity restored", protectedResourceName),
                    onHalfOpen: () =>
                        logger.LogWarning("CircuitBreaker HALF-OPEN | Testing {Resource} connectivity...", protectedResourceName));
        }

        public static AsyncRetryPolicy BuildRetryPolicy
        (
            ILogger logger,
            string protectedResourceName,
            ResiliencePolicyKey? policyKey = null,
            int retryCount = 3,
            Func<int, TimeSpan>? sleepDurationProvider = null
        )
        {
            return Policy
                .Handle<Exception>(exception => IsTransientException(exception, policyKey))
                .WaitAndRetryAsync(
                    retryCount: retryCount,
                    sleepDurationProvider: sleepDurationProvider ?? CalculateSleepDuration,
                    onRetry: (exception, delay, attempt, ctx) =>
                        logger.LogWarning(
                            "RetryAttempt {Attempt} | {Resource} | Waiting {Delay}ms | Error: {Error}",
                            attempt, protectedResourceName, delay.TotalMilliseconds, exception.Message));
        }

        private static TimeSpan CalculateSleepDuration(int attempt)
        {
            var delaySeconds = Math.Pow(2, attempt);
            return TimeSpan.FromSeconds(delaySeconds);
        }

        private static bool IsTransientException(Exception exception, ResiliencePolicyKey? policyKey)
        {
            switch (policyKey)
            {
                case ResiliencePolicyKey.Sql:
                    return IsTransientSQLException(exception);
                case ResiliencePolicyKey.Aws:
                    return IsTransientAWSException(exception);
                case ResiliencePolicyKey.AnthropicAPI:
                    return IsTransientAnthropicApiException(exception);
                case ResiliencePolicyKey.VoyageAPI:
                    return IsTransientVoyageApiException(exception);
                case ResiliencePolicyKey.RedisAPI:
                    return IsTransientRedisApiException(exception);
                default:
                    return false;
            }

        }

        private static bool IsTransientSQLException(Exception exception)
        {
            if (exception is SqlException sqlEx)
            {
                return _sqlServerTransientErrorNumbers.Contains(sqlEx.Number);
            }

            return AreThereAnyTransientHttpExceptions(exception);
        }

        private static bool IsTransientAWSException(Exception exception)
        {
            if (exception is AmazonServiceException awsEx)
            {

                if (awsEx.ErrorType == ErrorType.Receiver)
                    return true;

                return IsKnownTransientStatusCode(awsEx.StatusCode) ||
                       awsEx.StatusCode == HttpStatusCode.RequestTimeout;
            }

            return AreThereAnyTransientHttpExceptions(exception);
        }

        internal static bool IsTransientAnthropicApiException(Exception exception)
        {
            if (exception is Anthropic5xxException || exception is AnthropicRateLimitException)
                return true;

            if (exception is AnthropicIOException)
                return true;

            return AreThereAnyTransientHttpExceptions(exception);
        }

        internal static bool IsTransientVoyageApiException(Exception exception)
        {
            if (exception is HttpRequestException httpEx)
            {
                if (httpEx.StatusCode is null)  // connection failures: DNS failure, connection refused, TSL handshake failure
                    return true;

                return IsKnownTransientStatusCode(httpEx.StatusCode) ||
                   httpEx.StatusCode == HttpStatusCode.InternalServerError;
            }

            return AreThereAnyTransientHttpExceptions(exception);
        }

        internal static bool IsTransientRedisApiException(Exception exception)
        {
            if (exception is RedisConnectionException || exception is RedisTimeoutException)
                return true;

            if (exception is RedisServerException && ContainsKnownTransientRedisServerError(exception.Message))
                return true;

            return AreThereAnyTransientHttpExceptions(exception);
        }

        private static bool ContainsKnownTransientRedisServerError(string message)
        {
            if (string.IsNullOrEmpty(message))
                return false;

            string[] transientRedisServerKeywords = { "LOADING", "OOM" };

            return transientRedisServerKeywords.Any(keyword => message.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsKnownTransientStatusCode(HttpStatusCode? statusCode)
        {
            return statusCode == HttpStatusCode.TooManyRequests ||     // 429 - throttling
                   statusCode == HttpStatusCode.BadGateway ||          // 502
                   statusCode == HttpStatusCode.ServiceUnavailable ||  // 503
                   statusCode == HttpStatusCode.GatewayTimeout;        // 504
        }

        private static bool AreThereAnyTransientHttpExceptions(Exception exception)
        {
            if (exception is HttpRequestException || exception is TimeoutException)
                return true;

            if (ContainsTransientKeyword(exception.Message))
                return true;

            if (exception.GetType().Name.Contains("Transient", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private static bool ContainsTransientKeyword(string message)
        {
            if (string.IsNullOrEmpty(message))
                return false;

            string[] transientKeywords = { "transient", "retryable", "temporarily unavailable", "timeout", "throttl" };

            return transientKeywords.Any(keyWord => message.Contains(keyWord, StringComparison.OrdinalIgnoreCase));
        }
    }
}
