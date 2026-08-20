using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingApp.Infrastructure;
using TradingApp.Infrastructure.Interfaces;

var configuration = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();

var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(configuration);
services.AddLogging(builder => builder.AddConsole());
services.AddVoyageEmbeddingServices();
services.AddRedisConnection();
services.AddChunkingIngestionService();

var serviceProvider = services.BuildServiceProvider();

var sourceFiles = new[]
{
    @"C:\workspace\TradingApp-AWS\TradingApp.Infrastructure\Services\IntegrationEventPublisher.cs",
    @"C:\workspace\TradingApp-AWS\TradingApp.Infrastructure\ServiceCollectionExtensions.cs",
    @"C:\workspace\TradingApp-AWS\TradingApp.API\Hubs\AiChatHub.cs"
};

var chunkIngestionService = serviceProvider.GetRequiredService<IChunkIngestionService>();

var chunkedRecords = chunkIngestionService.ReadAndChunkSourceFiles(sourceFiles);

await chunkIngestionService.EmbedChunkedRecordsAsync(chunkedRecords);

await chunkIngestionService.PersistChunkedRecordsToRedisAsync(chunkedRecords);

foreach (var group in chunkedRecords.GroupBy(c => c.SourceFile))
{
    Console.WriteLine($"{group.Key}: {group.Count()} chunks embedded, {group.First().Embedding.Length} dimensions each");
}

partial class Program { };

