using System.Threading;
using System.Threading.Tasks;
using TradingApp.Events.Events;

namespace TradingApp.API.PushDispatch
{
    public delegate Task<PushEventOutcome> PushEventCallback(string eventType, IntegrationEvent integrationEvent, CancellationToken cancellationToken);
}
