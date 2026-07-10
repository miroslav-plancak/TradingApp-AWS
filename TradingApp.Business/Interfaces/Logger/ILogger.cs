using System;

namespace TradingApp.Business.Interfaces.Logger
{
    public interface ILogger
    {
        void LogInformation(string message);
        void LogInformation(string message, params object[] arguments);
        void LogWarning(string message);
        void LogWarning(string messageTemplate, params object[] args);
        void LogError(string message, Exception ex = null);
        void LogError(Exception ex, string messageTemplate, params object[] args);
        void SetClassScope(string className);
        void SetControllerScope(string controllerName);
        void SetMethodScope(string methodName);
        void LogWarning(Exception ex, string message);
        IDisposable BeginScope();
    }
}
