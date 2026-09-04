using System;
using System.Threading.Tasks;

namespace TradingApp.Business.Interfaces.Services.Helpers
{
    public interface IResiliencePolicyGuard
    {
        Task GuardViaResiliencePolicyAsync(Func<Task> sqlOperation, string operationName);
        Task<TResult> GuardViaResiliencePolicyAsync<TResult>(Func<Task<TResult>> sqlOperation, string operationName);
    }
}
