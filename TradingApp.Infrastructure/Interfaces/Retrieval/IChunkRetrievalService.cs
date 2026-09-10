using TradingApp.Infrastructure.Models.Retrieval;
﻿using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Interfaces.Retrieval
{
    public interface IChunkRetrievalService
    {
        Task<RetrievalResult> RetrieveRelevantContextAsync
        (
            string userQuery,
            Guid conversationId
        );
    }
}
