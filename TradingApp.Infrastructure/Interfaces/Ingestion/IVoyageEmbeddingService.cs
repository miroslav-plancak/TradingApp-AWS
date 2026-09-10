using TradingApp.Infrastructure.Interfaces.Ingestion;
﻿namespace TradingApp.Infrastructure.Interfaces.Ingestion
{
    public interface IVoyageEmbeddingService
    {
        Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
    }
}
