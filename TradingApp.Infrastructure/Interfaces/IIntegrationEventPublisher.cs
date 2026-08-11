using Amazon.Lambda.Core;
using TradingApp.Domain.Models.Enums;
using TradingApp.Events.Events;

namespace TradingApp.Infrastructure.Interfaces
{
    public interface IIntegrationEventPublisher
    {
        Task PublishToTopicAsync<TEvent>(TEvent eventPayload, string subject, ILambdaContext context, Action? simulateTopicFailure = null) where TEvent : IntegrationEvent;
        Task<ProcessedOrderStatusOutcome> PublishToTopicWithResponseAsync<TEvent>(TEvent eventPayload, string subject, ILambdaContext context, Action? simulateTopicFailure = null) where TEvent : IntegrationEvent;
    }
}
