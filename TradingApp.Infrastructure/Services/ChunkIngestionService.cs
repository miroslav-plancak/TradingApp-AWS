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

        public List<ChunkRecord> ReadAndChunkSourceFiles(string[] sourceFiles)
        {
            foreach (var filePath in sourceFiles)
            {
                var fileText = File.ReadAllText(filePath);
                var chunks = TextChunker.ChunkText(fileText);

                foreach (var chunk in chunks)
                {
                    chunkedRecords.Add(new ChunkRecord
                    {
                        Id = chunkedRecords.Count,
                        SourceFile = Path.GetFileName(filePath),
                        Content = chunk
                    });
                }
                
            }

            _logger.LogInformation("Total chunks across all files: {ChunkedRecordsCount}", chunkedRecords.Count);

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
    }
}
