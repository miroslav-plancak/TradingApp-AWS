using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Helpers.Retrieval
{
    public static class SystemPromptBuilder
    {
        private const string CompactionSystemInstruction =
            "Compact the given conversation messages into a single summary that preserves:\n\n" +
            "- how things work and why (mechanisms and reasoning), not just conclusions\n" +
            "- decisions made and the constraints behind them\n\n" +
            "You may be given an existing summary in addition to new messages - merge them into one " +
            "updated summary rather than treating them separately. Only include information actually " +
            "present in the provided content; do not infer or invent details.";

        private const string ChatSystemInstruction =
                "Answer the user's question using the following code context (if it's relevant) " +
                "and additional summary (if it is provided). If the context doesn't contain the " +
                "answer, say so instead of guessing.\n\n";

        public const string QueryRouteSystemInstruction =
           "Classify the following question about a codebase as either BROAD " +
           "(asking for an overview, end-to-end explanation, or how something works as a whole) " +
            "or NARROW (asking about one specific fact, value, or line)." +
           " Respond with exactly one word: BROAD or NARROW.";

        public const string ArbiterSystemInstruction =
          "Decide whether the user's question can be FULLY and accurately answered using only the chunks provided below - " +
          "not merely related to them, but sufficient to answer completely. Each chunk is a JSON object with a \"Key\" field.\r\n" +
          "Respond with exactly this JSON shape: {\"chunkKeys\": [...]}. Provide no explanation for your choice - pure JSON only.\r\n" +
          "- If one or more chunks together are sufficient to fully answer the question, list the \"Key\" value of each chunk you used.\r\n" +
          "- If the chunks are only partially relevant, or you are not confident they fully cover the question, return {\"chunkKeys\": []}.\r\n" +
          "- Only cite \"Key\" values that literally appear in the chunks provided - never invent one.";

        public const string QueryDecompositionSystemInstruction =
            "Decide whether the user's message asks about multiple distinct topics/entities, or just one - even if phrased in a complex or detailed way. " +
            "A single, detailed question about one topic (e.g. \"explain in detail how X handles retries\") is still ONE query. " +
            "Only split when the message genuinely names 2+ distinct topics/entities to address separately (e.g. \"compare how X handles retries vs how Y handles retries\").\r\n" +
            "Respond with exactly a JSON array of strings, nothing else - one element if it's a single query, one element per sub-topic if it's compound. " +
            "Each element must be a complete, standalone question (not a fragment) that could be searched on its own without the rest of the message for context.\r\n" +
            "Example (single query): [\"How does OutboxProcessingService handle retries?\"]\r\n" +
            "Example (multiple queries): [\"How does OutboxProcessingService handle retries?\", \"How does the SNS publish path handle retries?\"]\r\n" +
            "Do not infer or add topics that are not present in the user's message.";

        private const string AgenticChatSystemInstructionTemplate =
           "You are answering the user's question about a codebase. You have two tools available: " +
           "decompose_query, for splitting a genuinely compound question into its distinct sub-topics, and " +
           "search_knowledge_base, for retrieving relevant code context for a specific question. Only use " +
           "decompose_query when the question names 2+ distinct topics to address separately - for a single-topic " +
           "question, call search_knowledge_base directly. Use either tool as many times as needed before answering.\n\n" +
           "Your response has a hard output limit of {0} tokens. Structure your answer so it comfortably " +
           "finishes within that budget: cover the core mechanism for each part of the question, but favor " +
           "breadth over exhaustive depth on any single part. If a detail would meaningfully deepen the answer " +
           "but risks running long, leave it out rather than risk an incomplete answer - the user can ask a " +
           "targeted follow-up question about that part instead.";


        public static string BuildAgenticChatSystemPrompt(int maxResponseTokens)
        {
            return string.Format(AgenticChatSystemInstructionTemplate, maxResponseTokens);
        }

        public static string BuildCompactionSystemPrompt(string? existingSummary)
        {
            var summary = FormatExistingSummary(existingSummary);

            return string.Join("\n\n", new string?[] { CompactionSystemInstruction, summary }.Where(section => !string.IsNullOrWhiteSpace(section)));
        }

        public static string BuildChatSystemPrompt(RetrievalResult retrievalResult, string? existingSummary = "")
        {
            var summary = FormatExistingSummary(existingSummary);

            var chunks = string.Join("\n\n", retrievalResult.ChunkFallbacks.Select(c => $"Source: {c.SourceFile}\n{c.Content}"));
            var fullFiles = string.Join("\n\n", retrievalResult.FullFileContents.Select(c => $"FullFiles - FileName: {c.Key}\n{c.Value}"));
            var fullContext = string.Join("\n\n", new string?[] { chunks, fullFiles, summary }.Where(section => !string.IsNullOrWhiteSpace(section)));

            return $"{ChatSystemInstruction}{fullContext}";
        }

        private static string? FormatExistingSummary(string? existingSummary)
        {
            return string.IsNullOrWhiteSpace(existingSummary)
                ? null
                : $"Existing summary of earlier conversation:\n{existingSummary}";
        }
    }
}
