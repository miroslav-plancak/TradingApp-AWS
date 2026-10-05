using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TradingApp.Infrastructure.Helpers.Ingestion;
using TradingApp.Infrastructure.Interfaces.Ingestion;
using TradingApp.Infrastructure.Models.Ingestion;

namespace TradingApp.Infrastructure.Services.Ingestion
{
    public class ChunkIngestionService : IChunkIngestionService
    {
        private readonly IVoyageEmbeddingService _voyageEmbeddingService;
        private readonly ICorpusFileDescriptionService _corpusFileDescriptionService;
        private readonly IConnectionMultiplexer _connectionMultiplexer;
        private readonly IDatabase _database;
        private readonly ILogger<ChunkIngestionService> _logger;

        private const int FullIndexCap = 2500;

        public ChunkIngestionService
        (
            IVoyageEmbeddingService voyageEmbeddingService,
            ICorpusFileDescriptionService corpusFileDescriptionService,
            IConnectionMultiplexer connectionMultiplexer,
            ILogger<ChunkIngestionService> logger
        )
        {
            _voyageEmbeddingService = voyageEmbeddingService;
            _corpusFileDescriptionService = corpusFileDescriptionService;
            _connectionMultiplexer = connectionMultiplexer;
            _logger = logger;
            _database = _connectionMultiplexer.GetDatabase();
        }

        public async Task PersistEntireSourceFilesCorpusAsync(string[] sourceFiles)
        {
            List<RawCorpusSourceFile> rawCorpusSourceFiles = [];

            foreach (var sourceFile in sourceFiles)
            {
                rawCorpusSourceFiles.Add(new RawCorpusSourceFile
                {
                    Name = Path.GetFileName(sourceFile),
                    Content = File.ReadAllText(sourceFile)
                });
            }

            var descriptions = await _corpusFileDescriptionService.ProcessRawCorpusSourceFilesAsync([.. rawCorpusSourceFiles]);
            var descriptionsByName = descriptions.ToDictionary(x => x.Name, x => x.Description);

            var processedCorpusSourceFiles = rawCorpusSourceFiles.Select(rawFile => new ProcessedCorpusSourceFile
            {
                Name = rawFile.Name,
                Description = descriptionsByName.TryGetValue(rawFile.Name, out var description) ? description : string.Empty,
                IsFullyIndexed = rawFile.Content.Length < FullIndexCap
            }).ToList();

            await _database.KeyDeleteAsync("corpus:sourcefiles");
            await _database.SetAddAsync("corpus:sourcefiles", [.. processedCorpusSourceFiles.Select(x => new RedisValue(x.Name))]);

            foreach (var processedFile in processedCorpusSourceFiles)
            {
                await _database.HashSetAsync($"corpus:file:{processedFile.Name}",
                    [
                        new HashEntry("description", processedFile.Description),
                        new HashEntry("isfullyindexed", processedFile.IsFullyIndexed)
                    ]);
            }

            _logger.LogInformation("Total source files across entire corpus added: {SourceFilesCount}", processedCorpusSourceFiles.Count);
        }

        public async Task<List<ChunkRecord>> ReadAndChunkSourceFiles(string[] sourceFiles)
        {
            List<ChunkRecord> chunkedRecords = [];

            var processedSourceFiles = BuildProcessedSourceFiles(sourceFiles);

            if (processedSourceFiles.Count != 0)
            {
                var fullFileRecords = BuildFullFileRecordList(processedSourceFiles.Where(x => !x.ExceedsFullIndexCap).ToList());

                await PersistFullFileRecordsAsync(fullFileRecords);

                foreach (var file in processedSourceFiles)
                {
                    var chunks = TextChunker.ChunkText(file.FileContent);
                    var chunkIndex = 0;

                    foreach (var chunk in chunks)
                    {
                        chunkedRecords.Add(new ChunkRecord
                        {
                            Id = chunkedRecords.Count,
                            ChunkIndex = chunkIndex++,
                            TotalChunkCount = chunks.Count,
                            SourceFile = file.FileName,
                            Content = chunk
                        });
                    }

                }

                _logger.LogInformation("Total chunks across all files: {ChunkedRecordsCount}", chunkedRecords.Count);
            }
            else
            {
                _logger.LogError("SourceFiles array is empty: {SourceFilesLength}", sourceFiles.Length);
            }

            return chunkedRecords;
        }

        public async Task<List<ChunkRecord>> EmbedChunkedRecordsAsync(List<ChunkRecord> chunkedRecords)
        {
            var embeddings = await _voyageEmbeddingService.EmbedBatchAsync(chunkedRecords.Select(c => c.Content).ToList());

            for (var i = 0; i < chunkedRecords.Count; i++)
            {
                chunkedRecords[i].Embedding = embeddings[i];
            }

            return chunkedRecords;
        }

        public async Task PersistChunkedRecordsToRedisAsync(List<ChunkRecord> chunkedRecords)
        {
            foreach (var chunkRecord in chunkedRecords)
            {
                await _database.HashSetAsync($"chunk:{chunkRecord.Id}",
                    [
                        new HashEntry("sourceFile", chunkRecord.SourceFile),
                        new HashEntry("chunkIndex", chunkRecord.ChunkIndex),
                        new HashEntry("content", chunkRecord.Content),
                        new HashEntry("totalChunkCount", chunkRecord.TotalChunkCount),
                        new HashEntry("embedding", EmbeddingPacker.RePackEmbeddingFromFloatToByte(chunkRecord.Embedding))
                    ]);
            }
            _logger.LogInformation("Wrote {ChunkedRecordsCount} chunks to Redis.", chunkedRecords.Count);
        }

        private List<FullFileRecord> BuildFullFileRecordList(List<ProcessedSourceFile> processedSourceFiles)
        {
            List<FullFileRecord> fullFileRecords = [];

            foreach (var file in processedSourceFiles)
            {
                fullFileRecords.Add(new FullFileRecord
                {
                    FileName = file.FileName,
                    Content = file.FileContent
                });
            }

            return fullFileRecords;
        }

        private async Task PersistFullFileRecordsAsync(List<FullFileRecord> fullFileRecords)
        {
            foreach (var fullFileRecord in fullFileRecords)
            {
                await _database.HashSetAsync($"file:{fullFileRecord.FileName}",
                    [
                        new HashEntry("content", fullFileRecord.Content),
                    ]);
            }
            _logger.LogInformation("Wrote {FullFileRecordsCount} to Redis.", fullFileRecords.Count);
        }

        // 2500 is roughly the average .cs file size in this codebase - though "average" is a bit misleading 
        // here, since a handful of huge files (OutboxProcessingService,ScheduledOrderStatusProcessor etc..)
        // drag the number way up. Most files are nowhere near this size. This cap only decides whether we
        // bother indexing the whole file text in addition to chunking it - chunking itself always happens
        // regardless of where a file lands against this number.
        private static List<ProcessedSourceFile> BuildProcessedSourceFiles(string[] sourceFiles) =>

            sourceFiles.Select(sourceFile =>
            {
                var fileContent = File.ReadAllText(sourceFile);
                return new ProcessedSourceFile
                {
                    FileName = Path.GetFileName(sourceFile),
                    FileContent = fileContent,
                    ExceedsFullIndexCap = fileContent.Length >= FullIndexCap
                };
            }).ToList();
    }
}
