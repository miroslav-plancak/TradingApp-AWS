using TradingApp.Infrastructure.Models.Ingestion;
﻿namespace TradingApp.Infrastructure.Models.Ingestion
{
    public class FullFileRecord
    {
        public required string FileName { get; set; }
        public required string Content { get; set; }
    }
}
