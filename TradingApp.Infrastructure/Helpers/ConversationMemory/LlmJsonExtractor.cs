namespace TradingApp.Infrastructure.Helpers.ConversationMemory
{
    public static class LlmJsonExtractor
    {
        public static string ExtractJsonObject(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');

            if (start == -1 || end == -1 || end < start)
            {
                return text;
            }

            return text.Substring(start, end - start + 1);
        }
    }
}
