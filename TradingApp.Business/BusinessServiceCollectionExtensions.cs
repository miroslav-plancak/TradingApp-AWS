using Microsoft.Extensions.DependencyInjection;
using TradingApp.Business.Interfaces.Repositories;
using TradingApp.Business.Interfaces.Services.Helpers;
using TradingApp.Business.Interfaces.Services.Regular;
using TradingApp.Business.Interfaces.Services.Regular.Conversation;
using TradingApp.Business.Middleware;
using TradingApp.Business.Repositories;
using TradingApp.Business.Services.Helpers;
using TradingApp.Business.Services.Regular;
using TradingApp.Business.Services.Regular.Conversation;

namespace TradingApp.Business
{
    public static class BusinessServiceCollectionExtensions
    {
        public static IServiceCollection RegisterBusiness(this IServiceCollection services)
        {
            services.AddTransient<ExceptionHandlingMiddleware>();

            services.AddScoped<IOrderRepository, OrderRepository>()
                    .AddScoped<IDeadLetterRepository, DeadLetterRepository>()
                    .AddScoped<IOutboxMessageRepository, OutboxMessageRepository>()
                    .AddScoped<IConversationRepository, ConversationRepository>()
                    .AddScoped<IConversationChunkRepository, ConversationChunkRepository>()
                    .AddScoped<IConversationFullFileRepository, ConversationFullFileRepository>();


            services.AddScoped<IOrderService, OrderService>()
                    .AddScoped<IDeadLetterService, DeadLetterService>()
                    .AddScoped<IOutboxMessageService, OutboxMessageService>()
                    .AddScoped<IConversationService, ConversationService>()
                    .AddScoped<IConversationChunkService, ConversationChunkService>()
                    .AddScoped<IConversationFullFileService, ConversationFullFileService>()
                    .AddScoped<IConversationCompactionBoundaryService, ConversationCompactionBoundaryService>()
                    .AddScoped<IConversationMessageService, ConversationMessageService>()
                    .AddScoped<IConversationSummaryService, ConversationSummaryService>();

            services.AddSingleton<IResiliencePolicyGuard, ResiliencePolicyGuard>();
            services.AddSingleton<IResilienceConversationPolicyGuard, ResilienceConversationPolicyGuard>();

            return services;
        }
    }
}
