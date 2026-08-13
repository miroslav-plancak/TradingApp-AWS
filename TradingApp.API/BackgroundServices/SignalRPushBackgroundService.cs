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
using TradingApp.Business.Interfaces.Services;
using TradingApp.Events.Events;

namespace TradingApp.API.BackgroundServices
{
    public class SignalRPushBackgroundService : BackgroundService
    {
        private readonly IHubContext<EventsHub> _hubContext;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SignalRPushBackgroundService> _logger;

        private readonly string _queueUrl;
        private readonly Dictionary<string, PushEventCallback> _eventRegistry;

        public SignalRPushBackgroundService
        (
            IHubContext<EventsHub> hubContext,
            IServiceScopeFactory scopeFactory,
            ILogger<SignalRPushBackgroundService> logger
        )
        {
            _hubContext = hubContext;
            _scopeFactory = scopeFactory;
            _logger = logger;
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

             }, stoppingToken);
        }

        private async Task<PushEventOutcome> PushEventTypeDispatcher(string eventType, IntegrationEvent integrationEvent, CancellationToken cancellationToken)
        {
            try
            {
                if (!_eventRegistry.TryGetValue(eventType, out PushEventCallback handler))
                {
                    _logger.LogError($"Supplied eventType key: {eventType} was not found in the eventRegistry.");
                    return PushEventOutcome.INVALIDEVENTREGISTRYKEY;

                }

                return await handler(eventType, integrationEvent, cancellationToken);
            }
            catch (KeyNotFoundException)
            {
                return PushEventOutcome.FAILURE;
            }
            catch (Exception)
            {
                throw;
            }
        }

        private async Task<PushEventOutcome> OrderEventTypeHandlerAsync(string eventType, IntegrationEvent integrationEvent, CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();

                var orderService = scope.ServiceProvider.GetRequiredService<IOrderService>();
                var order = await orderService.GetOrderByClientOrderIdAsync(integrationEvent.ClientOrderId);
                await _hubContext.Clients.All.SendAsync(nameof(OrderStatusChangedEvent), order, cancellationToken);

                _logger.LogInformation($"OrderEventTypeHandlerAsync pushed {nameof(OrderStatusChangedEvent)} successfully.");

                return PushEventOutcome.SUCCESS;
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogWarning($"OrderEventTypeHandlerAsync failed pushing {nameof(OrderStatusChangedEvent)}, reason: {ex} ");
                return PushEventOutcome.FAILURE;
            }
            catch (Exception ex)
            {
                throw new Exception($"OrderEventTypeHandlerAsync has encountered a general failure.", ex);
            }

        }

        private async Task<PushEventOutcome> OutboxMessageEventTypeHandlerAsync(string eventType, IntegrationEvent integrationEvent, CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();

                var outboxMessageService = scope.ServiceProvider.GetRequiredService<IOutboxMessageService>();
                var outboxMessage = await outboxMessageService.GetByClientOrderIdAsync(integrationEvent.ClientOrderId);
                await _hubContext.Clients.All.SendAsync(nameof(OutboxMessageProcessedEvent), outboxMessage, cancellationToken);

                _logger.LogInformation($"OutboxMessageEventTypeHandlerAsync pushed {nameof(OutboxMessageProcessedEvent)} successfully.");

                return PushEventOutcome.SUCCESS;
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogWarning($"OutboxMessageEventTypeHandlerAsync failed pushing {nameof(OutboxMessageProcessedEvent)}, reason: {ex} ");
                return PushEventOutcome.FAILURE;
            }
            catch (Exception ex)
            {
                throw new Exception($"OutboxMessageEventTypeHandlerAsync has encountered a general failure.", ex);
            }
        }

        private async Task<PushEventOutcome> DeadLetterLogEventTypeHandlerAsync(string eventType, IntegrationEvent integrationEvent, CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();

                var deadLetterLogService = scope.ServiceProvider.GetRequiredService<IDeadLetterService>();
                var deadLetterLog = await deadLetterLogService.GetByClientOrderIdAsync(integrationEvent.ClientOrderId);
                await _hubContext.Clients.All.SendAsync(nameof(DeadLetterLogPersistedEvent), deadLetterLog, cancellationToken);

                _logger.LogInformation($"DeadLetterLogEventTypeHandlerAsync pushed {nameof(DeadLetterLogPersistedEvent)} successfully.");

                return PushEventOutcome.SUCCESS;
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogWarning($"DeadLetterLogEventTypeHandlerAsync failed pushing {nameof(DeadLetterLogPersistedEvent)}, reason: {ex} ");
                return PushEventOutcome.FAILURE;
            }
            catch (Exception ex)
            {
                throw new Exception($"DeadLetterLogEventTypeHandlerAsync has encountered a general failure.", ex);
            }
        }

    }
}
