using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Helpers.Retrieval
{
    public static class SystemPromptBuilder
    {
        public const string SearchCapReachedMessage =
            "No new results found in the last 3 attempts on this line of inquiry - " +
            "stop retrying this specific angle and either move on or answer with what you already have.";

        public const string NoFullFileContentAvailable = "No full file content available for";

        public const string NoSourceFileNamesFound = "No source file names found.";

        public const string ManifestAbsentFileReminder =
            "\n\nIf the file you are looking for is not in the list above, it is not indexed in this knowledge base. " +
            "Do not retry search_knowledge_base or get_full_file for that file's content - answer from what you " +
            "already have, or state plainly that it isn't in the knowledge base.";

        public const string MaxTokensContinuationMessage =
            "Your previous response was cut off mid-way because it ran out of tokens. " +
            "Here is exactly what you had generated so far:\n\n\"{0}\"\n\n" +
            "Continue generating from exactly where that text left off. Do not repeat any of it, and do not add any preamble, " +
            "acknowledgement, or commentary before continuing - just resume the answer directly.";

        private const string CompactionSystemInstruction =
            "Compact the given conversation messages into a single summary that preserves:\n\n" +
            "- how things work and why (mechanisms and reasoning), not just conclusions\n" +
            "- decisions made and the constraints behind them\n\n" +
            "You may be given an existing summary in addition to new messages - merge them into one " +
            "updated summary rather than treating them separately. Only include information actually " +
            "present in the provided content; do not infer or invent details.";
        //TODO: remove this once you clean up AiChatHub from the old implementaion
        private const string ChatSystemInstruction =
                "Answer the user's question using the following code context (if it's relevant) " +
                "and additional summary (if it is provided). If the context doesn't contain the " +
                "answer, say so instead of guessing.\n\n";

        public const string QueryRouteSystemInstruction =
           "Classify the following question about a codebase as either BROAD " +
           "(asking for an overview, end-to-end explanation, or how something works as a whole) " +
            "or NARROW (asking about one specific fact, value, or line)." +
           " Respond with exactly one word: BROAD or NARROW.";

        public const string ChunkArbiterSystemInstruction =
          "Decide whether the user's question can be FULLY and accurately answered using only the chunks provided below - " +
          "not merely related to them, but sufficient to answer completely. Each chunk is a JSON object with a \"Key\" field.\r\n" +
          "Respond with exactly this JSON shape: {\"chunkKeys\": [...]}. Provide no explanation for your choice - pure JSON only.\r\n" +
          "- If one or more chunks together are sufficient to fully answer the question, list the \"Key\" value of each chunk you used.\r\n" +
          "- If the chunks are only partially relevant, or you are not confident they fully cover the question, return {\"chunkKeys\": []}.\r\n" +
          "- Only cite \"Key\" values that literally appear in the chunks provided - never invent one.";

        public const string FullFileArbiterSystemInstruction =
          "Decide whether the user's question can be FULLY and accurately answered using only the full files provided below - " +
          "not merely related to them, but sufficient to answer completely. Each full file is a JSON object with a \"SourceFile\" field.\r\n" +
          "Respond with exactly this JSON shape: {\"sourceFiles\": [...]}. Provide no explanation for your choice - pure JSON only.\r\n" +
          "- If one or more full files together are sufficient to fully answer the question, list the \"SourceFile\" value of each full file you used.\r\n" +
          "- If the full files are only partially relevant, or you are not confident they fully cover the question, return {\"sourceFiles\": []}.\r\n" +
          "- Only cite \"SourceFile\" values that literally appear in the full files provided - never invent one.";

        public const string QueryDecompositionSystemInstruction =
            "Decide whether the user's message asks about multiple distinct topics/entities, or just one - even if phrased in a complex or detailed way. " +
            "A single, detailed question about one topic (e.g. \"explain in detail how X handles retries\") is still ONE query. " +
            "Only split when the message genuinely names 2+ distinct topics/entities to address separately (e.g. \"compare how X handles retries vs how Y handles retries\").\r\n" +
            "Respond with exactly a JSON array of strings, nothing else - one element if it's a single query, one element per sub-topic if it's compound. " +
            "Each element must be a complete, standalone question (not a fragment) that could be searched on its own without the rest of the message for context.\r\n" +
            "Example (single query): [\"How does OutboxProcessingService handle retries?\"]\r\n" +
            "Example (multiple queries): [\"How does OutboxProcessingService handle retries?\", \"How does the SNS publish path handle retries?\"]\r\n" +
            "Do not infer or add topics that are not present in the user's message.";

        public const string CorpusFileDescriptionSystemInstruction =
            "Given a numbered list of source files below, each with a FileName and its full Content, produce a " +
            "single, brief, one-sentence description of what each file actually does or is responsible for - " +
            "not a restatement of its name, a genuine summary of its real responsibility.\r\n" +
            "Respond with exactly this JSON shape: [{\"name\": \"...\", \"description\": \"...\"}, ...] - pure JSON only, no explanation.\r\n" +
            "- You MUST return exactly one object per file provided, in any order - never skip a file, never merge two files into one entry, never add an entry for a file not provided.\r\n" +
            "- The \"name\" value must exactly match the FileName given for that file - never invent or alter one.\r\n" +
            "- Each \"description\" should be short enough to scan quickly (roughly one sentence) but specific enough to judge whether the file is relevant to a given topic - name the concrete responsibility " +
            "(e.g. \"retries SNS topic publishes that failed and were deferred\"), not a vague restatement like \"handles processing.\"\r\n" +
            "Example: [{\"name\": \"OrderMapper.cs\", \"description\": \"Maps between Order entities and their request/response DTOs.\"}, " +
            "{\"name\": \"IDeadlLetterService.cs\", \"description\": \"Defines the contract for creating, querying, and resolving dead-letter logs.\"}]";

        private const string AgenticChatSystemInstruction =
         "You are answering the user's question about a codebase. You can infer the context from the chat history " +
         "(if provided), as well as with additional summary of chat history:\n{1}.\n\n" +

         "Tools available:\n" +
         "1. decompose_query - splits a question that names 2+ distinct topics into separate, focused sub-questions. " +
         "Only use it when the question genuinely covers multiple topics to address separately.\n" +

         "2. get_database_context - checks whether relevant code has already been fetched earlier in this " +
         "conversation, before running a fresh search.\n" +

         "3. search_knowledge_base - retrieves relevant code context for a specific, focused question.\n" +

         "4. get_full_file - fetches a complete file when a chunk-level excerpt isn't enough.\n" +

         "5. get_all_indexed_files - returns the complete manifest of every file currently indexed, including a " +
         "one-sentence description of what each file does and whether its full content can be fetched. Call this " +
         "when search_knowledge_base returns nothing relevant for a topic and you're unsure whether the content is " +
         "indexed at all, before retrying with different search phrasing. Use each file's description to judge " +
         "plausible relevance - you usually won't know the exact file name in advance. If nothing looks related, " +
         "the content is NOT indexed - stop searching for it. Do not retry search_knowledge_base or call " +
         "get_full_file for a file that is not in this list; treat its absence as final, not as a reason to " +
         "search harder.\n\n" +

         "Chunks returned by search_knowledge_base and get_database_context each include two flags:\n" +
         "- IsFullFileReconstructable: true means the chunks you already have together form the complete file - no " +
         "need to call get_full_file.\n" +
         "- IsFullFileIndexed: true means the full file could be fetched if still needed; false means don't call it, " +
         "it will return no content.\n" +
         "If IsFullFileReconstructable is true, you already have everything. If IsFullFileIndexed is true but " +
         "IsFullFileReconstructable is false, calling get_full_file is worth it.\n\n" +

         "Flow: for each focused topic, check get_database_context first, then fall back to search_knowledge_base " +
         "(and get_full_file if needed) if it doesn't have enough. Use any tool as many times as needed before " +
         "answering, but if repeated attempts on the same angle keep turning up nothing new, stop retrying it and " +
         "either move on to a different angle or answer with what you already have.\n\n" +

         "Your response has a hard output limit of {0} tokens. Structure your answer so it comfortably finishes " +
         "within that budget: cover the core mechanism for each part of the question, but favor breadth over " +
         "exhaustive depth on any single part. If a detail would meaningfully deepen the answer but risks running " +
         "long, leave it out rather than risk an incomplete answer - the user can ask a targeted follow-up question " +
         "about that part instead.";

        public static string BuildAgenticChatSystemPrompt(int maxResponseTokens, string? compactedSummary = " No compacted summary provided.")
        {
            return string.Format(AgenticChatSystemInstruction, maxResponseTokens, compactedSummary);
        }

        public static string BuildCompactionSystemPrompt(string? existingSummary)
        {
            var summary = FormatExistingSummary(existingSummary);

            return string.Join("\n\n", new string?[] { CompactionSystemInstruction, summary }.Where(section => !string.IsNullOrWhiteSpace(section)));
        }

        public static string BuildMaxTokensContinuationMessage(string assistantResponse)
        {
            return string.Format(MaxTokensContinuationMessage, assistantResponse);
        }
        //TODO: remove this once you clean up AiChatHub from the old implementaion
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
