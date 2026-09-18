using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingApp.Infrastructure.Helpers
{
    public static class AnthropicErrorMessageParser
    {
        public static string ExtractMessage(string anthropicApiResponseBody)
        {
            if (string.IsNullOrWhiteSpace(anthropicApiResponseBody)) return anthropicApiResponseBody;

            try
            {
                var deserializedResponse = JsonSerializer.Deserialize<AnthropicApiErrorResponse>(anthropicApiResponseBody);

                if (!string.IsNullOrEmpty(deserializedResponse?.Error?.Message))
                {
                    return deserializedResponse.Error.Message;
                }
            }
            catch (JsonException) { }

            return anthropicApiResponseBody;
        }

        private class AnthropicApiErrorResponse
        {
            [JsonPropertyName("type")]
            public string Type { get; set; } = string.Empty;

            [JsonPropertyName("error")]
            public ErrorDetails? Error { get; set; }

            [JsonPropertyName("request_id")]
            public string RequestId { get; set; } = string.Empty;
        }

        private class ErrorDetails
        {
            [JsonPropertyName("type")]
            public string Type { get; set; } = string.Empty;

            [JsonPropertyName("message")]
            public string Message { get; set; } = string.Empty;
        }
    }
}
