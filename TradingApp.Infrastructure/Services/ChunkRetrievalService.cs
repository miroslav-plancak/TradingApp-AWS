using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Text;
using System.Text.RegularExpressions;
using TradingApp.Infrastructure.Helpers;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Services
{
    public class ChunkRetrievalService : IChunkRetrievalService
    {
        private readonly IVoyageEmbeddingService _voyageEmbeddingService;
        private readonly IVoyageRerankService _voyageRerankService;
        private readonly IConnectionMultiplexer _connectionMultiplexer;
        private readonly ILogger<ChunkRetrievalService> _logger;
        private readonly IFileDebugLogger _fileDebugLogger;
        private readonly IDatabase _database;

        private const double RelevanceFloor = 0.53;
        private const double ReciprocalRankFusionK = 60;

        public ChunkRetrievalService
        (
            IVoyageEmbeddingService voyageEmbeddingService,
            IVoyageRerankService voyageRerankService,
            IConnectionMultiplexer connectionMultiplexer,
            ILogger<ChunkRetrievalService> logger,
            IFileDebugLogger fileDebugLogger
        )
        {
            _voyageEmbeddingService = voyageEmbeddingService;
            _voyageRerankService = voyageRerankService;
            _connectionMultiplexer = connectionMultiplexer;
            _logger = logger;
            _fileDebugLogger = fileDebugLogger;
            _database = connectionMultiplexer.GetDatabase();
        }

        public async Task<RetrievalResult> RetrieveRelevantContextAsync(string userQuestion)
        {
            var retrievedKNNChunks = await SearchKnnChunksAsync(userQuestion);

            var retrievedLexicalChunks = await SearchLexicalChunksAsync(userQuestion);

            var unifiedChunks = UnifyChunksFromBothSearchQueries(retrievedKNNChunks, retrievedLexicalChunks);

            var (knnChunksRankMap, lexicalChunksRankMap) = ComputeChunksRankMaps(retrievedKNNChunks, retrievedLexicalChunks);

            var unifiedChunksSortedByRrfScore = SortUnifiedChunksByRrfScore(unifiedChunks, knnChunksRankMap, lexicalChunksRankMap);

            var rerankedChunks = await RerankRetrievedChunksAsync(userQuestion, unifiedChunksSortedByRrfScore);

            rerankedChunks.RemoveAll(chunk => chunk.RelevanceScore < RelevanceFloor);

            if (rerankedChunks.Count == 0)
            {
                _logger.LogInformation(
                    "Query: {userQuestion} | No chunks cleared the relevance floor ({RelevanceFloor}) - returning empty context",
                    userQuestion, RelevanceFloor);

                await _fileDebugLogger.LogSectionAsync("rag-retrieval-after-filtering", $"Query: {userQuestion}",
                    "No chunks cleared the relevance floor - returning empty context.");

                return new RetrievalResult { ChunkFallbacks = [], FullFileContents = [] };
            }

            var filesEligibleForExpansion = await DetermineFilesEligibleForExpansionAsync(rerankedChunks);

            rerankedChunks.RemoveAll(chunk => filesEligibleForExpansion.Keys.Contains(chunk.SourceFile ?? string.Empty));

            var filteredRetrievedChunks = rerankedChunks
                 .GroupBy(x => x.SourceFile ?? string.Empty)
                 .SelectMany(group => group
                 .Take(3))
                 .ToList();

            LogRedisSearchResults(filteredRetrievedChunks, filesEligibleForExpansion, userQuestion);

            var retrievalResult = new RetrievalResult { ChunkFallbacks = filteredRetrievedChunks, FullFileContents = filesEligibleForExpansion };

            await _fileDebugLogger.LogSectionAsync("rag-retrieval-after-filtering", $"Query: {userQuestion}",
                FormatRetrievalResultIntoFileLog(retrievalResult));

            return retrievalResult;
        }

        private async Task<List<RetrievedChunk>> SearchKnnChunksAsync(string userQuestion)
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


        private async Task<List<RetrievedChunk>> SearchLexicalChunksAsync(string userQuestion)
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

        private static List<RetrievedChunk> UnifyChunksFromBothSearchQueries(List<RetrievedChunk> retrievedKnnChunks, List<RetrievedChunk> retrievedLexicalChunks)
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

        private static (Dictionary<string, int>, Dictionary<string, int>) ComputeChunksRankMaps
        (
            List<RetrievedChunk> retrievedKnnChunks, List<RetrievedChunk> retrievedLexicalChunks
        )
        {
            var knnChunksRankMap = retrievedKnnChunks
               .Select((chunk, index) => (chunk.Key, Rank: index + 1))
               .ToDictionary(kvp => kvp.Key!, kvp => kvp.Rank);

            var lexicalChunksRankMap = retrievedLexicalChunks
                .Select((chunk, index) => (chunk.Key, Rank: index + 1))
                .ToDictionary(kvp => kvp.Key!, kvp => kvp.Rank);

            return (knnChunksRankMap, lexicalChunksRankMap);
        }

        private static List<RetrievedChunk> SortUnifiedChunksByRrfScore
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

        private async Task<List<RetrievedChunk>> RerankRetrievedChunksAsync(string userQuestion, List<RetrievedChunk> retrievedChunks)
        {
            await _fileDebugLogger.LogSectionAsync("rag-retrieval-pre-reranking", $"Query: {userQuestion}",
                FormatRetrievalResultIntoFileLog(new RetrievalResult { ChunkFallbacks = retrievedChunks }));

            var rerankResults = await _voyageRerankService.RerankAsync(userQuestion, retrievedChunks.Select(x => x.Content ?? string.Empty).ToList());
            var rerankedChunks = rerankResults
                .OrderByDescending(r => r.RelevanceScore)
                .Select(x =>
                {
                    var retrievedChunk = retrievedChunks[x.Index];
                    retrievedChunk.RelevanceScore = x.RelevanceScore;
                    return retrievedChunk;
                })
                .ToList();

            await _fileDebugLogger.LogSectionAsync("rag-retrieval-post-reranking", $"Query: {userQuestion}",
              FormatRerankResultIntoFileLog(rerankResults.ToList()));

            return rerankedChunks;
        }

        private static string FormatRetrievalResultIntoFileLog(RetrievalResult retrievalResult)
        {
            var helperDivider = new string('-', 80);
            var sb = new StringBuilder();
            var maxChunks = retrievalResult.ChunkFallbacks.Count;
            var maxFullFiles = retrievalResult.FullFileContents.Count;

            if (retrievalResult.ChunkFallbacks.Count != 0)
            {
                sb.AppendLine(helperDivider);
                var chunkNumberCounter = 0;
                foreach (var chunk in retrievalResult.ChunkFallbacks)
                {
                    sb.AppendLine($"CHUNK #{chunkNumberCounter}");
                    sb.AppendLine($"SOURCEFILE: {chunk.SourceFile}");
                    sb.AppendLine();
                    sb.AppendLine($"KNNSCORE: {chunk.KnnScore}");
                    sb.AppendLine($"LEXICALSCORE: {chunk.LexicalScore}");
                    sb.AppendLine($"RELEVANCESCORE: {chunk.RelevanceScore}");
                    sb.AppendLine($"RECIPROCALRANKFUSIONSCORE: {chunk.ReciprocalRankFusionScore}");
                    sb.AppendLine();
                    sb.AppendLine($"CONTENT:\n{chunk.Content}");
                    chunkNumberCounter++;

                    if (maxChunks >= chunkNumberCounter)
                    {
                        sb.AppendLine(helperDivider);
                    }
                }

            }

            if (retrievalResult.FullFileContents.Count != 0)
            {
                sb.AppendLine(helperDivider);
                var fileNumberCounter = 0;
                foreach (var file in retrievalResult.FullFileContents)
                {
                    sb.AppendLine($"FILE #{fileNumberCounter}");
                    sb.AppendLine($"FILENAME: {file.Key}");
                    sb.AppendLine();
                    sb.AppendLine($"CONTENT:\n{file.Value}");
                    fileNumberCounter++;

                    if (maxFullFiles > fileNumberCounter)
                    {
                        sb.AppendLine(helperDivider);
                    }
                }

            }

            return sb.ToString();
        }

        private static string FormatRerankResultIntoFileLog(List<RerankResult> rerankResults)
        {
            var helperDivider = new string('-', 80);
            var sb = new StringBuilder();
            var maxRerankResults = rerankResults.Count;

            if (rerankResults.Count != 0)
            {
                sb.AppendLine(helperDivider);
                var rerankResultCounter = 0;

                foreach (var rerankResult in rerankResults)
                {
                    sb.AppendLine($"INDEX: {rerankResult.Index}");
                    sb.AppendLine($"RELEVANCE SCORE: {rerankResult.RelevanceScore}");
                    rerankResultCounter++;

                    if (maxRerankResults > rerankResultCounter)
                    {
                        sb.AppendLine(helperDivider);
                    }
                }
            }

            return sb.ToString();
        }

        private async Task<Dictionary<string, string>> DetermineFilesEligibleForExpansionAsync(List<RetrievedChunk> rerankedChunks)
        {
            var fileOccurrenceMap = rerankedChunks.GroupBy(x => x.SourceFile ?? string.Empty).ToDictionary(g => g.Key, g => g.Count());

            var distinctFileNames = rerankedChunks.DistinctBy(x => x.SourceFile).Select(x => x.SourceFile);
            var filesWithFullContent = await GetExistingFullFileContentsMapAsync(distinctFileNames);

            var filesEligibleForExpansion = filesWithFullContent
                        .Where(kvp => fileOccurrenceMap[kvp.Key] >= 2)
                        .ToDictionary(x => x.Key, x => x.Value);

            return filesEligibleForExpansion;
        }

        private async Task<Dictionary<string, string>> GetExistingFullFileContentsMapAsync(IEnumerable<string?> distinctFileNames)
        {
            var allFetchedFileContents = await GetSourceFileContentsAsync(distinctFileNames.OfType<string>());
            var filesWithContent = allFetchedFileContents.Where(x => x.Value != string.Empty).ToDictionary(x => x.Key, x => x.Value);
            return filesWithContent;
        }

        private async Task<Dictionary<string, string>> GetSourceFileContentsAsync(IEnumerable<string> sourceFiles)
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

        private void LogRedisSearchResults(List<RetrievedChunk> filteredRetrievedChunks, Dictionary<string, string> filesEligibleForExpansion, string userQuestion)
        {
            _logger.LogInformation("Query: {userQuestion}", userQuestion);

            foreach (var chunk in filteredRetrievedChunks)
            {
                _logger.LogInformation("SourceFile: {SourceFile} | knnScore={knnScore} | lexicalScore={lexicalScore} | relevanceScore={relevanceScore}",
                    chunk.SourceFile?.ToString(), chunk.KnnScore?.ToString(), chunk.LexicalScore?.ToString(), chunk.RelevanceScore.ToString());
            }

            foreach (var expandedFile in filesEligibleForExpansion.Keys)
            {
                _logger.LogInformation("Expanded full file: {SourceFile}", expandedFile);
            }
        }
    }
}
