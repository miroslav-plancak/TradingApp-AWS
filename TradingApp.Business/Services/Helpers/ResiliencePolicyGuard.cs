using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using System;
using System.Threading.Tasks;
using TradingApp.Business.Interfaces.Services;

namespace TradingApp.Business.Services.Helpers
{
    public class ResiliencePolicyGuard : IResiliencePolicyGuard
    {
        private readonly IAsyncPolicy _resiliencePolicy;
        private readonly ILogger<ResiliencePolicyGuard> _logger;
        public ResiliencePolicyGuard(IAsyncPolicy resiliencePolicy, ILogger<ResiliencePolicyGuard> logger)
        {
            _resiliencePolicy = resiliencePolicy;
            _logger = logger;
        }

        public async Task GuardViaResiliencePolicyAsync(Func<Task> sqlOperation, string operationName)
        {
            try
            {
                await _resiliencePolicy.ExecuteAsync(sqlOperation);
            }
            catch (BrokenCircuitException)
            {
                _logger.LogWarning("CircuitOpen | Database unreachable | Stopping write | Operation: {Operation}", operationName);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GeneralError | Repository write failed | Operation: {Operation}", operationName);
                throw;
            }
        }

        public async Task<TResult> GuardViaResiliencePolicyAsync<TResult>(Func<Task<TResult>> sqlOperation, string operationName)
        {
            try
            {
                return await _resiliencePolicy.ExecuteAsync(sqlOperation);
            }
            catch (BrokenCircuitException)
            {
                _logger.LogWarning("CircuitOpen | Database unreachable | Stopping query | Operation: {Operation}", operationName);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GeneralError | Repository query failed | Operation: {Operation}", operationName);
                throw;
            }
        }
    }
}
