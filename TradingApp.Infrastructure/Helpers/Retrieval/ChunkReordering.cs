using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Helpers.Retrieval
{
    public static class ChunkReordering
    {
        public static List<RetrievedChunk> ReorderChunksToUShape(List<RetrievedChunk> chunks)
        {
            var sortedByRelevanceScore = chunks.OrderByDescending(x => x.RelevanceScore).ToList();

            return ApplyUShapeOrder(sortedByRelevanceScore);
        }

        public static Dictionary<string, string> ReorderFullFilesToUShape
        (
            Dictionary<string, string> filesEligibleForExpansion,
            List<RetrievedChunk> filteredRetrievedChunksForPersistance
        )
        {
            var fileRelevanceScores = filteredRetrievedChunksForPersistance
                .GroupBy(x => x.SourceFile ?? string.Empty)
                .ToDictionary(group => group.Key, group => group.Max(chunk => chunk.RelevanceScore));

            var uShapedFileNames = ApplyUShapeOrder(
                fileRelevanceScores.OrderByDescending(x => x.Value).Select(x => x.Key).ToList());

            var uShapedFullFileContents = new Dictionary<string, string>();

            foreach (var fileName in uShapedFileNames)
            {
                if (filesEligibleForExpansion.TryGetValue(fileName, out var content))
                {
                    uShapedFullFileContents.Add(fileName, content);
                }
            }

            return uShapedFullFileContents;
        }

        private static List<T> ApplyUShapeOrder<T>(List<T> orderedByRelevanceDescending)
        {
            return orderedByRelevanceDescending
                    .Where((_, index) => index % 2 == 0)
                    .Concat(
                        orderedByRelevanceDescending.Where((_, index) => index % 2 != 0).Reverse()
                    ).ToList();
        }
    }
}
