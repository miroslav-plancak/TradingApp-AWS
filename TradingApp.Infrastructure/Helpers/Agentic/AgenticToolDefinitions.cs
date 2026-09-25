using Anthropic.Models.Messages;
using System.Text.Json;

namespace TradingApp.Infrastructure.Helpers.Agentic
{
    public static class AgenticToolDefinitions
    {
        public static readonly Tool SearchKnowledgeBase = new Tool
        {
            Name = "search_knowledge_base",

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

                Required = new List<string> { "query"}
            }
        };

        public static readonly Tool DecomposeQuery = new Tool
        {
            Name = "decompose_query",

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

                Required =  new List<string> { "question" }
            }
        };
    }
}
