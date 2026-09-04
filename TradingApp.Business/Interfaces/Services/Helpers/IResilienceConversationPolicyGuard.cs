using System;
using System.Threading.Tasks;

namespace TradingApp.Business.Interfaces.Services.Helpers
{
    public interface IResilienceConversationPolicyGuard
    {
        Task GuardViaResiliencePolicyAsync(Func<Task> sqlOperation, string operationName);
        Task<TResult> GuardViaResiliencePolicyAsync<TResult>(Func<Task<TResult>> sqlOperation, string operationName);
    }
}
