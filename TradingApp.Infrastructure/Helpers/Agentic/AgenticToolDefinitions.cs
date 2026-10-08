using Anthropic.Models.Messages;
using System.Text.Json;
using TradingApp.Infrastructure.Enums;
using TradingApp.Infrastructure.Helpers.Retrieval;

namespace TradingApp.Infrastructure.Helpers.Agentic
{
    public static class AgenticToolDefinitions
    {
        public static readonly Tool DecomposeQuery = new Tool
        {
            Name = AgenticTool.decompose_query.ToString(),

            Description =
                "Splits a question covering multiple distinct topics into a list of focused, standalone " +
                "sub-questions — one per topic. Call this before search_knowledge_base only when the question " +
                "genuinely names 2+ distinct topics to address separately. Do not call it for a single, " +
                "detailed question about one topic. Returns a list of sub-questions; call search_knowledge_base " +
                "once for each.",

            InputSchema = new InputSchema
            {
                Type = JsonSerializer.SerializeToElement("object"),

                Properties = new Dictionary<string, JsonElement>
                {
                    ["question"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "string",
                        description = "The original user question to evaluate for decomposition."
                    })
                },

                Required = new List<string> { "question" }
            }
        };

        public static readonly Tool SearchKnowledgeBase = new Tool
        {
            Name = AgenticTool.search_knowledge_base.ToString(),

            Description =
                "Searches the TradingApp-AWS codebase for information relevant to a single, focused question." +
                "Returns the most relevant code chunks, ranked by relevance. Call this once per distinct topic " +
                "- if the question covers multiple unrelated topics, call decompose_query first to get focused " +
                "sub-questions, then call this tool once per sub-question.",

            InputSchema = new InputSchema
            {
                Type = JsonSerializer.SerializeToElement("object"),

                Properties = new Dictionary<string, JsonElement>
                {
                    ["query"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "string",
                        description = "A focused, standalone question about one specific topic in the codebase."
                    }),
                },

                Required = new List<string> { "query" }
            }
        };

        public static readonly Tool GetFullFile = new Tool
        {
            Name = AgenticTool.get_full_file.ToString(),

            Description =
             "Fetches the complete, unabridged content of a single source file, when a chunk-level excerpt from " +
             "search_knowledge_base or get_database_context isn't enough — for example, understanding a whole " +
             "class's structure or confirming there's no other relevant logic elsewhere in the file. " +
             "Only call this for a fileName that appeared in a prior search_knowledge_base or get_database_context " +
             "chunk result with IsFullFileIndexed: true. Calling it for a file that wasn't returned by either tool, " +
             "or was returned with IsFullFileIndexed: false, will return no content and waste a call — check the " +
             "IsFullFileIndexed flag on the chunk first, from whichever tool returned it, before deciding this is " +
             "worth calling. Also check IsFullFileReconstructable on those same chunks first — if it's true, the " +
             "chunks you already have together already form the complete file, and calling this tool would be " +
             "redundant regardless of IsFullFileIndexed.",

            InputSchema = new InputSchema
            {
                Type = JsonSerializer.SerializeToElement("object"),

                Properties = new Dictionary<string, JsonElement>
                {
                    ["fileName"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "string",
                        description = "The exact file name as it appeared in the FileName field of a prior " +
                       "search_knowledge_base or get_database_context chunk result (e.g. 'OutboxProcessingService.cs'). Must match exactly."
                    }),
                },

                Required = new List<string> { "fileName" }
            }
        };

        public static readonly Tool GetDatabaseContext = new Tool
        {
            Name = AgenticTool.get_database_context.ToString(),

            Description =
              "Checks whether relevant code has already been fetched earlier in this conversation, before running a " +
              "fresh search. Call this once per focused topic - if the question covers multiple unrelated topics, call " +
              "decompose_query first, then call this tool once per sub-question. If it returns \"No re-usable context " +
              "found.\", or the returned context is insufficient to fully answer the question, fall back to " +
              "search_knowledge_base and get_full_file for that topic.",

            InputSchema = new InputSchema
            {
                Type = JsonSerializer.SerializeToElement("object"),

                Properties = new Dictionary<string, JsonElement>
                {
                    ["query"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "string",
                        description = "A focused, standalone question about one specific topic in the codebase."
                    }),
                },

                Required = new List<string> { "query" }
            }
        };

        public static readonly Tool GetAllIndexedFiles = new Tool
        {
            Name = AgenticTool.get_all_indexed_files.ToString(),

            Description =
               "Returns the complete manifest of every file currently indexed in the knowledge base - a table of " +
               "contents for what's available, not a targeted search. For each file this includes its FileName, a " +
               "one-sentence Description of what it actually does, and IsFullyIndexed (true means get_full_file can " +
               "fetch its complete content; false means don't call get_full_file for it, it will return no content). " +
               "Call this when search_knowledge_base returns nothing relevant for a topic and you're unsure whether " +
               "the content is indexed at all, before retrying with different search phrasing. Use the Description " +
               "field to judge relevance - you usually won't know the exact file name in advance, so look for a " +
               "plausible match based on what each file does, not an exact name match. If nothing in the manifest " +
               "looks related, the tool call result returns " +
               $"\"{SystemPromptBuilder.NoSourceFileNamesFound}\", " +
               "treat that as a strong signal the content isn't indexed, and prefer telling the user you don't have this " +
               " information over continuing to retry search_knowledge_base or get_full_file with different wording.",
              
            InputSchema = new InputSchema
            {
                Type = JsonSerializer.SerializeToElement("object")
            }
        };
    }
}
