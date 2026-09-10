using TradingApp.Infrastructure.Models.Retrieval;
﻿namespace TradingApp.Infrastructure.Models.Retrieval
{
    public class RerankResult
    {
        public int Index { get; set; }
        public double RelevanceScore { get; set; }
    }
}