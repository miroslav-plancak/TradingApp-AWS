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
    @"C:\workspace\TradingApp-AWS\TradingApp.Domain\Models\Enums\ResiliencePolicyKey.cs",          // 119 chars
    @"C:\workspace\TradingApp-AWS\TradingApp.Domain\Models\Enums\OrderStatus.cs",                  // 188 chars
    @"C:\workspace\TradingApp-AWS\TradingApp.Business\Interfaces\Repositories\IOrderRepository.cs", // 569 chars
    @"C:\workspace\TradingApp-AWS\TradingApp.API\Hubs\AiChatHub.cs",                                // 3,060 chars
    @"C:\workspace\TradingApp-AWS\TradingApp.API\Controllers\DeadLetterController.cs",              // 4,634 chars
    @"C:\workspace\TradingApp-AWS\TradingApp.Infrastructure\Services\IntegrationEventPublisher.cs", // 7,229 chars
    @"C:\workspace\TradingApp-AWS\TradingApp.Business\Services\Regular\OrderService.cs",            // 8,591 chars
    @"C:\workspace\TradingApp-AWS\TradingApp.Infrastructure\ServiceCollectionExtensions.cs",        // 12,164 chars
    @"C:\workspace\TradingApp-AWS\Functions\ScheduledOrderStatusProcessor\ScheduledOrderStatusProcessor.cs", // 14,175 chars
    @"C:\workspace\TradingApp-AWS\Functions\ScheduledOutboxMessageProcessor\Services\OutboxProcessingService.cs", // 21,201 chars 
};

var chunkIngestionService = serviceProvider.GetRequiredService<IChunkIngestionService>();

var chunkedRecords = await chunkIngestionService.ReadAndChunkSourceFiles(sourceFiles);

var embeddedRecords = await chunkIngestionService.EmbedChunkedRecordsAsync(chunkedRecords);

await chunkIngestionService.PersistChunkedRecordsToRedisAsync(embeddedRecords);

foreach (var group in embeddedRecords.GroupBy(c => c.SourceFile))
{
    Console.WriteLine($"{group.Key}: {group.Count()} chunks embedded, {group.First().Embedding.Length} dimensions each");
}

partial class Program { };

