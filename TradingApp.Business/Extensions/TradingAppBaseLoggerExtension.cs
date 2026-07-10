using System.Runtime.CompilerServices;
using TradingApp.Business.Interfaces.Logger;

namespace TradingApp.Business.Extensions
{
    public abstract class TradingAppBaseLoggerExtension<T>
    {
        protected readonly ILogger _logger;
        protected TradingAppBaseLoggerExtension(ILogger logger)
        {
            _logger = logger;
            _logger.SetClassScope(typeof(T).Name);
        }

        protected void LogEntryWithScope([CallerMemberName] string methodName = "")
        {
            _logger.SetClassScope(typeof(T).Name);
            _logger.SetMethodScope(methodName);

            _logger.LogInformation($"Starting execution of {typeof(T).Name}.{methodName} method.");
        }

        protected void LogExitWithScope([CallerMemberName] string methodName = "")
        {
            _logger.SetClassScope(typeof(T).Name);
            _logger.SetMethodScope(methodName);

            _logger.LogInformation($"Ending execution of {typeof(T).Name}.{methodName} method.");
        }
    }
}
