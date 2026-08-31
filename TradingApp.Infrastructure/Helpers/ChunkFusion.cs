using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Helpers
{
    /// <summary>
    /// Merges KNN and lexical search results into one deduplicated chunk list.
    /// </summary>
    public static class ChunkFusion
    {
        private const double ReciprocalRankFusionK = 60;

        public static List<RetrievedChunk> UnifyChunksFromBothSearchQueries(List<RetrievedChunk> retrievedKnnChunks, List<RetrievedChunk> retrievedLexicalChunks)
        {
            var unifiedChunks = retrievedKnnChunks
                .Concat(retrievedLexicalChunks)
                .GroupBy(chunk => chunk.Key)
                .Select(group => new RetrievedChunk
                {
                    Key = group.Key,
                    SourceFile = group.First().SourceFile,
                    Content = group.First().Content,
                    KnnScore = group.Select(x => x.KnnScore).FirstOrDefault(x => x != null),
                    LexicalScore = group.Select(c => c.LexicalScore).FirstOrDefault(score => score != null)
                }).ToList();

            return unifiedChunks;
        }

        // NOTE: ComputeChunksRankMaps and SortUnifiedChunksByRrfScore are an exercise in unification of two completely independent
        // search score results: KNN search score result + Lexical search score result, into a ReciprocalRankFusionScore, normallized
        // by the ReciprocalRankFusionK constant. This resulting score is then used to re-sort the input RetrievedChunks by it for no
        // other reason other than to demonstrate how it would look like if we had the need for the unification of search score results,
        // nothing, except the log downstream of it, is dependent on this sorting. It is left here as a reminder particularly because it
        // does not affect anything downstream of it as the RerankRetrievedChunksAsync does not care in which order are the RetrievedChunks
        // fed to it and the RelevanceFloor cut off is based off of RetrievedChunk's RelevanceScore which is set downstream of these two methods.
        public static (Dictionary<string, int>, Dictionary<string, int>) ComputeChunksRankMaps
        (
            List<RetrievedChunk> retrievedKnnChunks, List<RetrievedChunk> retrievedLexicalChunks
        )
        {
            var knnChunksRankMap = retrievedKnnChunks
               .Select((chunk, index) => (chunk.Key, Rank: index + 1))
               .Where(x => x.Key is not null)
               .GroupBy(x => x.Key)
               .ToDictionary(g => g.Key!, g => g.Min(x => x.Rank));

            var lexicalChunksRankMap = retrievedLexicalChunks
               .Select((chunk, index) => (chunk.Key, Rank: index + 1))
               .Where(x => x.Key is not null)
               .GroupBy(x => x.Key)
               .ToDictionary(g => g.Key!, g => g.Min(x => x.Rank));

            return (knnChunksRankMap, lexicalChunksRankMap);
        }

        public static List<RetrievedChunk> SortUnifiedChunksByRrfScore
        (
            List<RetrievedChunk> unifiedChunks,
            Dictionary<string, int> knnChunksRankMap,
            Dictionary<string, int> lexicalChunksRankMap
        )
        {
            foreach (var chunk in unifiedChunks)
            {
                var rrfScore = 0.0;

                if (knnChunksRankMap.TryGetValue(chunk.Key!, out var knnRank))
                {
                    rrfScore += 1.0 / (ReciprocalRankFusionK + knnRank);
                }

                if (lexicalChunksRankMap.TryGetValue(chunk.Key!, out var lexicalRank))
                {
                    rrfScore += 1.0 / (ReciprocalRankFusionK + lexicalRank);
                }

                chunk.ReciprocalRankFusionScore = rrfScore;
            }

            unifiedChunks = unifiedChunks
                .OrderByDescending(chunk => chunk.ReciprocalRankFusionScore)
                .ToList();

            return unifiedChunks;
        }
    }
}
