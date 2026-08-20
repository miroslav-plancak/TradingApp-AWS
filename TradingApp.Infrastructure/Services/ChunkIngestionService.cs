using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TradingApp.Infrastructure.Helpers;
using TradingApp.Infrastructure.Interfaces;
using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Services
{
    public class ChunkIngestionService : IChunkIngestionService
    {
        private readonly IVoyageEmbeddingService _voyageEmbeddingService;
        private readonly IConnectionMultiplexer _connectionMultiplexer;
        private readonly IDatabase _database;
        private readonly ILogger<ChunkIngestionService> _logger;

        private readonly List<ChunkRecord> chunkedRecords = [];
        private readonly List<FullFileRecord> fullFileRecords = [];

        public ChunkIngestionService
        (
            IVoyageEmbeddingService voyageEmbeddingService,
            IConnectionMultiplexer connectionMultiplexer,
            ILogger<ChunkIngestionService> logger
        )
        {
            _voyageEmbeddingService = voyageEmbeddingService;
            _connectionMultiplexer = connectionMultiplexer;
            _logger = logger;
            _database = _connectionMultiplexer.GetDatabase();
        }

        public async Task<List<ChunkRecord>> ReadAndChunkSourceFiles(string[] sourceFiles)
        {
            var processedSourceFiles = BuildProcessedSourceFiles(sourceFiles);

            if (processedSourceFiles.Count != 0)
            {
                var fullFileRecords = BuildFullFileRecordList(processedSourceFiles.Where(x => !x.ExceedsFullIndexCap).ToList());
                await PersistFullFileRecordsAsync(fullFileRecords);

                foreach (var file in processedSourceFiles)
                {
                    var chunks = TextChunker.ChunkText(file.FileContent);

                    foreach (var chunk in chunks)
                    {
                        chunkedRecords.Add(new ChunkRecord
                        {
                            Id = chunkedRecords.Count,
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
                        new HashEntry("content", chunkRecord.Content),
                        new HashEntry("sourceFile", chunkRecord.SourceFile),
                        new HashEntry("embedding", EmbeddingPacker.RePackEmbeddingFromFloatToByte(chunkRecord.Embedding))
                    ]);
            }
            _logger.LogInformation("Wrote {ChunkedRecordsCount} chunks to Redis.", chunkedRecords.Count);
        }

        private List<FullFileRecord> BuildFullFileRecordList(List<ProcessedSourceFile> processedSourceFiles)
        {
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
                    ExceedsFullIndexCap = fileContent.Length >= 2500
                };
            }).ToList();
    }
}
