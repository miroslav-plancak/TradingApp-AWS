using TradingApp.Infrastructure.Models.Retrieval;
﻿namespace TradingApp.Infrastructure.Models.Retrieval
{
    public class RetrievalResult
    {
        public List<RetrievedChunk> ChunkFallbacks { get; set; } = [];
        public Dictionary<string, string> FullFileContents { get; set; } = [];
    }
}
