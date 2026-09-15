using Amazon.SQS;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TradingApp.API.Hubs;
using TradingApp.API.PushDispatch;
using TradingApp.Business.DTOs.DeadLetter;
using TradingApp.Business.DTOs.Order;
using TradingApp.Business.DTOs.Outbox;
using TradingApp.Business.Interfaces.Services;
using TradingApp.Events.Events;

namespace TradingApp.API.BackgroundServices
{
    public class SignalRPushBackgroundService : BackgroundService
    {
        private readonly IHubContext<EventsHub> _hubContext;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SignalRPushBackgroundService> _logger;
        private readonly IAmazonSQS _sqsClient;

        private readonly string _queueUrl;
        private readonly Dictionary<string, PushEventCallback> _eventRegistry;

        public SignalRPushBackgroundService
        (
            IHubContext<EventsHub> hubContext,
            IServiceScopeFactory scopeFactory,
            ILogger<SignalRPushBackgroundService> logger,
            IAmazonSQS sqsClient
        )
        {
            _hubContext = hubContext;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _sqsClient = sqsClient;
            _queueUrl = Environment.GetEnvironmentVariable("SIGNALR_PUSH_QUEUE_URL")
                ?? throw new InvalidOperationException("SIGNALR_PUSH_QUEUE_URL environment variable is not set.");

            _eventRegistry = new()
            {
                ["OrderStatusChangedEvent"] = OrderEventTypeHandlerAsync,
                ["DeadLetterLogPersistedEvent"] = DeadLetterLogEventTypeHandlerAsync,
                ["OutboxMessageProcessedEvent"] = OutboxMessageEventTypeHandlerAsync
            };
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {

            await PushEventDispatchLoop.RunAsync(_logger, _queueUrl, async (eventType, integrationEvent, cancellationToken) =>
             {
                 return await PushEventTypeDispatcher(eventType, integrationEvent, cancellationToken);

             },_sqsClient, stoppingToken);
        }

        private async Task<PushEventOutcome> PushEventTypeDispatcher
        (
            string eventType,
            IntegrationEvent integrationEvent,
            CancellationToken cancellationToken
        )
        {
            try
            {
                if (!_eventRegistry.TryGetValue(eventType, out PushEventCallback handler))
                {
                    _logger.LogError($"Supplied eventType key: {eventType} was not found in the eventRegistry.");
                    return PushEventOutcome.InvalidEventRegistryKey;

                }

                return await handler(eventType, integrationEvent, cancellationToken);
            }
            catch (KeyNotFoundException)
            {
                return PushEventOutcome.Failure;
            }
        }

        private async Task<PushEventOutcome> DispatchEntityPushAsync<TService, TResult>
        (
            string eventName,
            Func<TService, Guid, Task<TResult>> fetch,
            IntegrationEvent integrationEvent,
            CancellationToken cancellationToken
        )
            where TService : notnull
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();

                var service = scope.ServiceProvider.GetRequiredService<TService>();
                var result = await fetch(service, integrationEvent.ClientOrderId);
                await _hubContext.Clients.All.SendAsync(eventName, result, cancellationToken);

                _logger.LogInformation("PushDispatchSucceeded | Event: {EventName}", eventName);

                return PushEventOutcome.Success;
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogWarning(ex, "PushDispatchFailed | Event: {EventName}", eventName);
                return PushEventOutcome.Failure;
            }
            catch (Exception ex)
            {
                throw new Exception($"PushDispatch encountered a general failure for event: {eventName}.", ex);
            }
        }

        private Task<PushEventOutcome> OrderEventTypeHandlerAsync
        (
            string eventType,
            IntegrationEvent integrationEvent,
            CancellationToken cancellationToken
        ) =>
            DispatchEntityPushAsync<IOrderService, OrderResponseDTO>(
                nameof(OrderStatusChangedEvent),
                (service, clientOrderId) => service.GetOrderByClientOrderIdAsync(clientOrderId),
                integrationEvent,
                cancellationToken);

        private Task<PushEventOutcome> OutboxMessageEventTypeHandlerAsync
        (
            string eventType,
            IntegrationEvent integrationEvent,
            CancellationToken cancellationToken
        ) =>
            DispatchEntityPushAsync<IOutboxMessageService, OutboxMessageResponseDTO>(
                nameof(OutboxMessageProcessedEvent),
                (service, clientOrderId) => service.GetByClientOrderIdAsync(clientOrderId),
                integrationEvent,
                cancellationToken);

        private Task<PushEventOutcome> DeadLetterLogEventTypeHandlerAsync
        (
            string eventType,
            IntegrationEvent integrationEvent,
            CancellationToken cancellationToken
        ) =>
            DispatchEntityPushAsync<IDeadLetterService, DeadLetterLogResponseDTO>(
                nameof(DeadLetterLogPersistedEvent),
                (service, clientOrderId) => service.GetByClientOrderIdAsync(clientOrderId),
                integrationEvent,
                cancellationToken);
    }
}
