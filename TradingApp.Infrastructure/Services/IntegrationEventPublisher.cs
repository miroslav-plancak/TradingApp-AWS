using Amazon.Lambda.Core;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;
using System.Text.Json;
using TradingApp.Domain;
using TradingApp.Domain.Models.Enums;
using TradingApp.Events.Events;
using TradingApp.Infrastructure.Interfaces;

namespace TradingApp.Infrastructure.Services
{
    public class IntegrationEventPublisher : IIntegrationEventPublisher
    {
        private readonly TradingDbContext _tradingDbContext;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly IAsyncPolicy _messagingResiliencePolicy;
        private readonly IAsyncPolicy _sqlResiliencePolicy;

        private readonly string _orderEventsTopicArn;

        public IntegrationEventPublisher
        (
            TradingDbContext tradingDbContext,
            IAmazonSimpleNotificationService snsClient,
            [FromKeyedServices(ResiliencePolicyKey.Aws)] IAsyncPolicy messagingResiliencePolicy,
            [FromKeyedServices(ResiliencePolicyKey.Sql)] IAsyncPolicy sqlResiliencePolicy
        )
        {
            _tradingDbContext = tradingDbContext;
            _snsClient = snsClient;
            _messagingResiliencePolicy = messagingResiliencePolicy;
            _sqlResiliencePolicy = sqlResiliencePolicy;

            _orderEventsTopicArn = Environment.GetEnvironmentVariable("ORDER_EVENTS_TOPIC_ARN")
             ?? throw new InvalidOperationException("ORDER_EVENTS_TOPIC_ARN environment variable is not set.");
        }

        public Task PublishToTopicAsync<TEvent>(TEvent eventPayload, string subject, ILambdaContext context, Action? simulateTopicFailure = null) where TEvent : IntegrationEvent
            => PublishToTopicWithResponseAsync(eventPayload, subject, context, simulateTopicFailure);

        public async Task<ProcessedOrderStatusOutcome> PublishToTopicWithResponseAsync<TEvent>(TEvent eventPayload, string subject, ILambdaContext context, Action? simulateTopicFailure = null) where TEvent : IntegrationEvent
        {
            try
            {
                var messageBody = JsonSerializer.Serialize(eventPayload);

                var request = new PublishRequest
                {
                    TopicArn = _orderEventsTopicArn,
                    Message = messageBody,
                    Subject = subject,
                    MessageGroupId = eventPayload.ClientOrderId.ToString(),
                    MessageDeduplicationId = Guid.NewGuid().ToString(),
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        { "EventType", new MessageAttributeValue
                            {
                                DataType = "String",
                                StringValue = eventPayload.GetType().Name
                            }
                        }
                    }
                };

                context.Logger.LogWarning(
                    $"PublishingEventToTopic | CorrelationId: {eventPayload.CorrelationId} | ClientOrderId: {eventPayload.ClientOrderId} | Topic: order_events_topic.fifo");

                await _messagingResiliencePolicy.ExecuteAsync(async () =>
                {
                    simulateTopicFailure?.Invoke();
                    await _snsClient.PublishAsync(request);
                });

                context.Logger.LogWarning(
                    $"EventPublishedToTopic | CorrelationId: {eventPayload.CorrelationId} | ClientOrderId: {eventPayload.ClientOrderId} | Topic: order_events_topic.fifo");

                return ProcessedOrderStatusOutcome.Filled;
            }
            catch (BrokenCircuitException)
            {
                context.Logger.LogWarning(
                    $"CircuitOpen | CorrelationId: {eventPayload.CorrelationId} | ClientOrderId: {eventPayload.ClientOrderId} " +
                    $"| Action: deferring publish to UnpublishedTopicMessages");

                return await TryPersistUnpublishedTopicMessageAsync(context, eventPayload);
            }
            catch (AmazonSimpleNotificationServiceException snsException)
            {
                context.Logger.LogError(
                    $"TopicPublishFailed | CorrelationId: {eventPayload.CorrelationId} | ClientOrderId: {eventPayload.ClientOrderId} | Error: {snsException.Message}");

                return await TryPersistUnpublishedTopicMessageAsync(context, eventPayload);
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"EventPublishFailed | CorrelationId: {eventPayload.CorrelationId} | ClientOrderId: {eventPayload.ClientOrderId} | Error: {ex.Message}");

                return await TryPersistUnpublishedTopicMessageAsync(context, eventPayload);
            }
        }

        private async Task<ProcessedOrderStatusOutcome> TryPersistUnpublishedTopicMessageAsync<TEvent>(ILambdaContext context, TEvent eventPayload) where TEvent : IntegrationEvent
        {
            try
            {
                var outcome = await _sqlResiliencePolicy.ExecuteAsync(async Task<ProcessedOrderStatusOutcome> () =>
                {
                    var rowExists = await _tradingDbContext.CheckIfUnpublishedTopicMessageExistsAsync<TEvent>(
                        eventPayload.ClientOrderId, eventPayload.CorrelationId);

                    if (rowExists == null)
                    {
                        await _tradingDbContext.SaveUnpublishedTopicMessageAsync(eventPayload);

                        context.Logger.LogWarning(
                    $"SavedToUnpublishedTopicMessages | CorrelationId: {eventPayload.CorrelationId} | ClientOrderId: {eventPayload.ClientOrderId}");

                        return ProcessedOrderStatusOutcome.FilledPublishDeferred;
                    }
                    else
                    {
                        context.Logger.LogWarning(
                              $"UnpublishedTopicMessageAlreadyExists | CorrelationId: {eventPayload.CorrelationId} | ClientOrderId: {eventPayload.ClientOrderId}");
                        return ProcessedOrderStatusOutcome.FilledPublishDeferred;
                    }

                });

                return outcome;
            }
            catch (BrokenCircuitException)
            {
                context.Logger.LogWarning(
                        $"CircuitOpen | FailedPersistingToUnpublishedTopicMessages | CorrelationId: {eventPayload.CorrelationId} | Database unreachable, stopping batch");
                return ProcessedOrderStatusOutcome.SqlCircuitOpen;
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
               $"UnexpectedError | CorrelationId: {eventPayload.CorrelationId} | UnpublishedTopicMessagePersisted, event not published | Error: {ex.Message}");

                return ProcessedOrderStatusOutcome.FilledButNotSavedNorPublished;
            }
        }
    }
}
