using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Helpers
{
    public static class SystemPromptBuilder
    {
        public static string BuildSystemPrompt(RetrievalResult retrievalResult)
        {
            var chunks = string.Join("\n\n", retrievalResult.ChunkFallbacks.Select(c => $"Source: {c.SourceFile}\n{c.Content}"));
            var fullFiles = string.Join("\n\n", retrievalResult.FullFileContents.Select(c => $"FullFiles - FileName: {c.Key}\n{c.Value}"));
            var fullContext = string.Join("\n\n", new[] { chunks, fullFiles }.Where(section => !string.IsNullOrWhiteSpace(section)));

            return $"Answer the user's question using the following code context if it's relevant." +
                $" If the context doesn't contain the answer, say so instead of guessing.\n\n{fullContext}";
        }
    }
}
