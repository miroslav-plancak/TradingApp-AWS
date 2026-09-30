using StackExchange.Redis;
using System.Text.RegularExpressions;
using TradingApp.Infrastructure.Models.Retrieval;

namespace TradingApp.Infrastructure.Helpers.Retrieval
{
    public static class RedisResultParser
    {
        public static List<RetrievedChunk> MapKnnSearchResultToRetrievedChunkList(RedisResult knnSearchResult)
        {
            var retrievedChunks = new List<RetrievedChunk>();

            for (var i = 1; i < knnSearchResult.Length; i += 2)
            {
                var key = (string?)knnSearchResult[i];
                var fieldMap = BuildFieldMap(knnSearchResult[i + 1]);

                retrievedChunks.Add(new RetrievedChunk
                {
                    Key = key,
                    ChunkIndex = ExtractChunkIndexFieldValue(fieldMap),
                    TotalChunkCount = ExtractTotalChunkCountFieldValue(fieldMap),
                    KnnScore = fieldMap.TryGetValue("score", out var score) && double.TryParse(score, out var knnScore) ? knnScore : null,
                    SourceFile = fieldMap.TryGetValue("sourceFile", out var sourceFile) ? sourceFile : string.Empty,
                    Content = fieldMap.TryGetValue("content", out var content) ? content : string.Empty
                });
            }

            return retrievedChunks;
        }

        public static string ParseUserMessage(string userMessage)
        {
            var terms = Regex.Split(userMessage, @"[^\w]+")
               .Where(t => t.Length > 0)
               .Where(IsValidIdentifier)
               .ToList();

            return string.Join("|", terms);
        }

        public static bool IsValidIdentifier(string token)
        {
            var hasUnderscore = token.Contains('_');
            var hasInternalCaps = token.Skip(1).Any(char.IsUpper);
            var isAllCaps = token.Length > 1 && token.All(char.IsUpper);

            return hasUnderscore || hasInternalCaps || isAllCaps;
        }

        public static List<RetrievedChunk> MapLexicalSearchResultToRetrievedChunkList(RedisResult lexicalSearchResult)
        {
            var retrievedChunks = new List<RetrievedChunk>();

            for (var i = 1; i < lexicalSearchResult.Length; i += 3)
            {
                var key = (string?)lexicalSearchResult[i];
                var lexicalScore = (double)lexicalSearchResult[i + 1];
                var fieldMap = BuildFieldMap(lexicalSearchResult[i + 2]);

                retrievedChunks.Add(new RetrievedChunk
                {
                    Key = key,
                    ChunkIndex = ExtractChunkIndexFieldValue(fieldMap),
                    TotalChunkCount = ExtractTotalChunkCountFieldValue(fieldMap),
                    LexicalScore = lexicalScore,
                    SourceFile = fieldMap.TryGetValue("sourceFile", out var sourceFile) ? sourceFile : string.Empty,
                    Content = fieldMap.TryGetValue("content", out var content) ? content : string.Empty
                });
            }

            return retrievedChunks;
        }

        private static int ExtractChunkIndexFieldValue(Dictionary<string, string>? fieldMap)
        {
            return fieldMap?.TryGetValue("chunkIndex", out var chunkIndex) == true
                                && int.TryParse(chunkIndex, out var chunkIndexResult) ? chunkIndexResult : 0;
        }

        private static int ExtractTotalChunkCountFieldValue(Dictionary<string, string>? fieldMap)
        {
            return fieldMap?.TryGetValue("totalChunkCount", out var totalChunkCount) == true
                                && int.TryParse(totalChunkCount, out var totalChunkCountResult) ? totalChunkCountResult : 0;
        }

        private static Dictionary<string, string> BuildFieldMap(RedisResult searchResult)
        {
            var fieldMap = new Dictionary<string, string>();

            for (var f = 0; f < searchResult.Length; f += 2)
            {
                fieldMap[(string)searchResult[f]!] = (string)searchResult[f + 1]!;

            }

            return fieldMap;
        }
    }
}
