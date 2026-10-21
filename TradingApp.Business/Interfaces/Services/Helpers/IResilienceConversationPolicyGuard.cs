using System;
using System.Threading;
using System.Threading.Tasks;

namespace TradingApp.Business.Interfaces.Services.Helpers
{
    public interface IResilienceConversationPolicyGuard
    {
        Task GuardViaResiliencePolicyAsync
        (
            Func<CancellationToken, Task> sqlOperation,
            string operationName,
            CancellationToken ct = default
        );
        Task<TResult> GuardViaResiliencePolicyAsync<TResult>
        (
            Func<CancellationToken, Task<TResult>> sqlOperation,
            string operationName,
            CancellationToken ct = default
        );
    }
}
