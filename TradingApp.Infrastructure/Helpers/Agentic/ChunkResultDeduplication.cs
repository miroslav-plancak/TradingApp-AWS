using Anthropic.Models.Messages;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Helpers.Agentic
{
    public static class ChunkResultDeduplication
    {
        public static List<string> RemoveNowRedundantChunksForFile
        (
            string fileName,
            List<MessageParam> messages,
            Dictionary<string, List<RetrievedChunk>> chunksByToolUseId
        )
        {
            var removalsLog = new List<string>();

            for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            {
                if (!messages[messageIndex].Content.TryPickContentBlockParams(out var blocks))
                {
                    continue;
                }

                var updatedBlocks = new List<ContentBlockParam>();
                var messageChanged = false;

                foreach (var block in blocks)
                {
                    if (block.TryPickToolResult(out var toolResult) && chunksByToolUseId.TryGetValue(toolResult.ToolUseID, out var chunks))
                    {
                        var remaining = chunks.Where(c => c.SourceFile != fileName).ToList();

                        if (remaining.Count != chunks.Count)
                        {
                            chunksByToolUseId[toolResult.ToolUseID] = remaining;

                            updatedBlocks.Add(new ToolResultBlockParam(toolResult.ToolUseID)
                            {
                                Content = FormatChunksForToolResult(remaining)
                            });

                            messageChanged = true;

                            removalsLog.Add(
                                $"Removed {chunks.Count - remaining.Count} chunk(s) for {fileName} from tool_use {toolResult.ToolUseID} " +
                                $"({remaining.Count} chunk(s) remain in that result)");

                            continue;
                        }
                    }

                    updatedBlocks.Add(block);
                }

                if (messageChanged)
                {
                    messages[messageIndex] = new MessageParam
                    {
                        Role = messages[messageIndex].Role,
                        Content = updatedBlocks
                    };
                }
            }

            return removalsLog;
        }

        public static string FormatChunksForToolResult(List<RetrievedChunk> chunks)
        {
            return string.Join("\n", chunks.Select((chunk, i) =>
                $"#{i + 1}\n Key: {chunk.Key} " +
                $"\n FileName: {chunk.SourceFile}" +
                $"\n RelevanceScore: {chunk.RelevanceScore}" +
                $"\n IsFullFileReconstructable: {chunk.IsFullFileReconstructable}" +
                $"\n IsFullFileIndexed: {chunk.IsFullFileIndexed}" +
                $"\n\n{chunk.Content} "
            ));
        }
    }
}
