using StackExchange.Redis;
using System.Text.RegularExpressions;
using TradingApp.Infrastructure.Helpers;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Services
{
    //TODO: revise logging 
    public class KnowledgeBaseQueryService : IKnowledgeBaseQueryService
    {
        private readonly IDatabase _database;
        private readonly IConnectionMultiplexer _connectionMultiplexer;
        private readonly IVoyageEmbeddingService _voyageEmbeddingService;

        public KnowledgeBaseQueryService(IConnectionMultiplexer connectionMultiplexer, IVoyageEmbeddingService voyageEmbeddingService)
        {
            _connectionMultiplexer = connectionMultiplexer;
            _database = _connectionMultiplexer.GetDatabase();
            _voyageEmbeddingService = voyageEmbeddingService;
        }

        public async Task<List<RetrievedChunk>> SearchKnnChunksAsync(string userQuestion)
        {
            var queryBytes = await EmbedQuestionAsync(userQuestion);

            var searchResult = await _database.ExecuteAsync(
             "FT.SEARCH", "idx:chunks",
             "*=>[KNN 10 @embedding $BLOB AS score]",
             "PARAMS", "2", "BLOB", queryBytes,
             "SORTBY", "score",
             "DIALECT", "2",
             "RETURN", "3", "sourceFile", "content", "score");

            var retrievedKNNChunks = MapKnnSearchResultToRetrievedChunkList(searchResult);

            return retrievedKNNChunks;
        }
        private async Task<byte[]> EmbedQuestionAsync(string userQuestion)
        {
            var queryEmbedding = await _voyageEmbeddingService.EmbedAsync(userQuestion);
            var queryBytes = EmbeddingPacker.RePackEmbeddingFromFloatToByte(queryEmbedding);

            return queryBytes;
        }

        private static List<RetrievedChunk> MapKnnSearchResultToRetrievedChunkList(RedisResult searchResult)
        {
            var retrievedChunks = new List<RetrievedChunk>();

            for (var i = 1; i < searchResult.Length; i += 2)
            {
                var key = (string?)searchResult[i];
                var fields = searchResult[i + 1];
                var fieldMap = new Dictionary<string, string>();

                for (var f = 0; f < fields.Length; f += 2)
                {
                    fieldMap[(string)fields[f]!] = (string)fields[f + 1]!;

                }

                retrievedChunks.Add(new RetrievedChunk
                {
                    Key = key,
                    KnnScore = fieldMap.TryGetValue("score", out var score) && double.TryParse(score, out var knnScore) ? knnScore : null,
                    SourceFile = fieldMap.TryGetValue("sourceFile", out var sourceFile) ? sourceFile : string.Empty,
                    Content = fieldMap.TryGetValue("content", out var content) ? content : string.Empty
                });
            }

            return retrievedChunks;
        }

        public async Task<List<RetrievedChunk>> SearchLexicalChunksAsync(string userQuestion)
        {
            var parsedUserQuestion = ParseUserQuestion(userQuestion);

            var lexicalSearchResult = await _database.ExecuteAsync(
                   "FT.SEARCH", "idx:chunks",
                   $"@content:({parsedUserQuestion})",
                   "SCORER", "BM25",
                   "WITHSCORES",
                   "RETURN", "2", "sourceFile", "content",
                   "LIMIT", "0", "10",
                   "DIALECT", "2");
            var retrievedLexicalChunks = MapLexicalSearchResultToRetrievedChunkList(lexicalSearchResult);
            return retrievedLexicalChunks;
        }


        private static string ParseUserQuestion(string userQuestion)
        {
            var terms = Regex.Split(userQuestion, @"[^\w]+")
               .Where(t => t.Length > 0)
               .Where(IsValidIdentifier)
               .ToList();

            return string.Join("|", terms);
        }

        private static bool IsValidIdentifier(string token)
        {
            var hasUnderscore = token.Contains('_');
            var hasInternalCaps = token.Skip(1).Any(char.IsUpper);
            var isAllCaps = token.Length > 1 && token.All(char.IsUpper);

            return hasUnderscore || hasInternalCaps || isAllCaps;
        }

        private static List<RetrievedChunk> MapLexicalSearchResultToRetrievedChunkList(RedisResult lexicalSearchResult)
        {
            var retrievedChunks = new List<RetrievedChunk>();

            for (var i = 1; i < lexicalSearchResult.Length; i += 3)
            {
                var key = (string?)lexicalSearchResult[i];
                var lexicalScore = (double)lexicalSearchResult[i + 1];
                var fields = lexicalSearchResult[i + 2];
                var fieldMap = new Dictionary<string, string>();

                for (var f = 0; f < fields.Length; f += 2)
                {
                    fieldMap[(string)fields[f]!] = (string)fields[f + 1]!;

                }

                retrievedChunks.Add(new RetrievedChunk
                {
                    Key = key,
                    LexicalScore = lexicalScore,
                    SourceFile = fieldMap.TryGetValue("sourceFile", out var sourceFile) ? sourceFile : string.Empty,
                    Content = fieldMap.TryGetValue("content", out var content) ? content : string.Empty
                });
            }

            return retrievedChunks;
        }

        public async Task<Dictionary<string, string>> GetSourceFileContentsAsync(IEnumerable<string> sourceFiles)
        {
            var distinctFiles = sourceFiles.Distinct().ToList();

            var fetchTasks = distinctFiles.Select(async sourceFile =>
            {
                var content = await _database.HashGetAsync($"file:{sourceFile}", "content");
                return (sourceFile, content);
            });

            var results = await Task.WhenAll(fetchTasks);

            return results.ToDictionary(r => r.sourceFile, r => (string?)r.content ?? string.Empty);
        }
    }
}
