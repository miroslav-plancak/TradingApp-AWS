using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Text;
using System.Threading.Tasks;
using TradingApp.Infrastructure.Helpers;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Services
{
    public class ChunkRetrievalService : IChunkRetrievalService
    {
        private readonly IVoyageEmbeddingService _voyageEmbeddingService;
        private readonly IConnectionMultiplexer _connectionMultiplexer;
        private readonly ILogger<ChunkRetrievalService> _logger;
        private readonly IFileDebugLogger _fileDebugLogger;
        private readonly IDatabase _database;
       
        private const double RelevanceCutoff = 0.485;
        public ChunkRetrievalService
        (
            IVoyageEmbeddingService voyageEmbeddingService,
            IConnectionMultiplexer connectionMultiplexer,
            ILogger<ChunkRetrievalService> logger,
            IFileDebugLogger fileDebugLogger
        )
        {
            _voyageEmbeddingService = voyageEmbeddingService;
            _connectionMultiplexer = connectionMultiplexer;
            _logger = logger;
            _fileDebugLogger = fileDebugLogger;
            _database = connectionMultiplexer.GetDatabase();
        }

        public async Task<RetrievalResult> RetrieveRelevantChunksAsync(string userQuestion)
        {
            var queryBytes = await EmbedQuestionAsync(userQuestion);

            var searchResult = await _database.ExecuteAsync(
             "FT.SEARCH", "idx:chunks",
             "*=>[KNN 10 @embedding $BLOB AS score]",
             "PARAMS", "2", "BLOB", queryBytes,
             "SORTBY", "score",
             "DIALECT", "2",
             "RETURN", "3", "sourceFile", "content", "score");

            var retrievedChunks = new List<RetrievedChunk>();

            for (var i = 1; i < searchResult.Length; i += 2)
            {
                var fields = searchResult[i + 1];
                var fieldMap = new Dictionary<string, string>();

                for (var f = 0; f < fields.Length; f += 2)
                {
                    fieldMap[(string)fields[f]!] = (string)fields[f + 1]!;

                }

                retrievedChunks.Add(new RetrievedChunk
                {
                    Score = fieldMap.TryGetValue("score", out var score) ? score : string.Empty,
                    SourceFile = fieldMap.TryGetValue("sourceFile", out var sourceFile) ? sourceFile : string.Empty,
                    Content = fieldMap.TryGetValue("content", out var content) ? content : string.Empty
                });
            }

            retrievedChunks.RemoveAll(chunk => double.TryParse( chunk.Score, out double result) && result >= RelevanceCutoff);

            var fileOccurrenceMap = retrievedChunks.GroupBy(x => x.SourceFile ?? string.Empty).ToDictionary(g => g.Key, g => g.Count());

            var distinctFileNames = retrievedChunks.DistinctBy(x => x.SourceFile).Select(x => x.SourceFile);
            var filesWithFullContent = await GetExistingFullFileContentsMapAsync(distinctFileNames);

            var filesEligibleForExpansion = filesWithFullContent
                        .Where(kvp => fileOccurrenceMap[kvp.Key] >= 2)
                        .ToDictionary(x => x.Key, x => x.Value);

            retrievedChunks.RemoveAll(chunk => filesEligibleForExpansion.Keys.Contains(chunk.SourceFile ?? string.Empty));

            var filteredRetrievedChunks = retrievedChunks
                 .GroupBy(x => x.SourceFile ?? string.Empty)
                 .SelectMany(group => group
                 .OrderBy(x => x.Score)
                 .Take(3))
                 .ToList();

            var totalMatches = searchResult[0];
            LogRedisSearchResults(retrievedChunks, userQuestion, totalMatches);

            var retrievalResult = new RetrievalResult { ChunkFallbacks = filteredRetrievedChunks, FullFileContents = filesEligibleForExpansion };
            await _fileDebugLogger.LogSectionAsync("rag-retrieval", $"Query: {userQuestion}", FormatRetrievalResultIntoFileLog(retrievalResult));

            return retrievalResult;
        }

        private async Task<byte[]> EmbedQuestionAsync(string userQuestion)
        {
            var queryEmbedding = await _voyageEmbeddingService.EmbedAsync(userQuestion);
            var queryBytes = EmbeddingPacker.RePackEmbeddingFromFloatToByte(queryEmbedding);

            return queryBytes;
        }

        private void LogRedisSearchResults(List<RetrievedChunk> results, string userQuestion, RedisResult totalMatches)
        {
            _logger.LogInformation("Query: {userQuestion} | candidates {totalMatches}", userQuestion, totalMatches);

            foreach (var result in results)
            {
                _logger.LogInformation("SourceFile: {SourceFile} | score={Score}", result.SourceFile?.ToString(), result.Score?.ToString());
            }
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

        private static string FormatRetrievalResultIntoFileLog(RetrievalResult retrievalResult)
        {
            var helperDivider = new string('-', 80);
            var sb = new StringBuilder();
            var maxChunks = retrievalResult.ChunkFallbacks.Count();
            var maxFullFiles = retrievalResult.FullFileContents.Count();

            if (retrievalResult.ChunkFallbacks.Count != 0)
            {
                sb.AppendLine(helperDivider);
                var chunkNumber = 1;
                foreach (var chunk in retrievalResult.ChunkFallbacks)
                {
                    sb.AppendLine($"CHUNK #{chunkNumber}");
                    sb.AppendLine($"SOURCEFILE: {chunk.SourceFile}");
                    sb.AppendLine($"SCORE: {chunk.Score}");
                    sb.AppendLine($"CONTENT:\n{chunk.Content}");
                    chunkNumber++;

                    if(maxChunks >= chunkNumber)
                    {
                        sb.AppendLine(helperDivider);
                    }
                }
              
            }

            if (retrievalResult.FullFileContents.Count != 0)
            {
                sb.AppendLine(helperDivider);
                var fileNumber = 1;
                foreach (var file in retrievalResult.FullFileContents)
                {
                    sb.AppendLine($"FILE #{fileNumber}");
                    sb.AppendLine($"FILE: {file.Key}");
                    sb.AppendLine($"CONTENT:\n{file.Value}");
                    fileNumber++;

                    if (maxFullFiles >= fileNumber)
                    {
                        sb.AppendLine(helperDivider);
                    }
                }
              
            }

            return sb.ToString();
        }
    }
}
