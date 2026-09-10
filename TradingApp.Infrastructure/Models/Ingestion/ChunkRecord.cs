using TradingApp.Infrastructure.Models.Ingestion;
﻿namespace TradingApp.Infrastructure.Models.Ingestion
{
    public class ChunkRecord
    {
        public required int Id { get; set; }
        public required string SourceFile { get; set; }
        public required string Content { get; set; }
        public float[] Embedding { get; set; } = [];
    }
}
