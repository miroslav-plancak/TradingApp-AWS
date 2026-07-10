using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using TradingApp.Business.Constants;
using TradingApp.Business.Interfaces.Logger;
namespace TradingApp.Business.Logger
{
    public class TradingAppLogger : Interfaces.Logger.ILogger
    {
        private readonly ILogger<TradingAppLogger>  _logger;
        private static readonly AsyncLocal<Dictionary<string, object>> _scopeData = new();
        private Dictionary<string, object> Scope 
        {
            get
            {
                if(_scopeData.Value == null)
                {
                    _scopeData.Value = new Dictionary<string, object>();
                }
                return _scopeData.Value;
            }
        }

        public TradingAppLogger(ILogger<TradingAppLogger> logger)
        {
            _logger = logger;
        }

        public IDisposable BeginScope()
        {
            return _logger.BeginScope("TradingAppLoggerScope");
        }

        public void LogError(string message, Exception ex = null)
        {
            if (ex != null)
            {
                _logger.LogError(ex, message);
            }

            _logger.LogError(message);
        }

        public void LogInformation(string message)
        {
            _logger.LogInformation(message);
        }

        public void LogInformation(string message, params object[] arguments)
        {
            using var scope = BeginScopeWithProperties();
            _logger.LogInformation(message, arguments);
        }

        public void LogWarning(string message)  
        {
           _logger.LogWarning(message);
        }

        public void SetClassScope(string className)
        {
            SetScopeValue(LoggingConstants.ClassNameScope, className);
        }

        public void SetControllerScope(string controllerName)
        {
            SetScopeValue(LoggingConstants.ControllerName, controllerName);
        }

        public void SetMethodScope(string methodName)
        {
            SetScopeValue(LoggingConstants.MethodNameScope, methodName);
        }

        public void LogWarning(Exception ex, string message)
        {
            using var scope = BeginScopeWithProperties();
            _logger.LogWarning(ex, message);
        }

        public void LogWarning(string messageTemplate, params object[] args)
        {
            using var scope = BeginScopeWithProperties();
            _logger.LogWarning(messageTemplate, args);
        }

        public void LogError(Exception ex, string messageTemplate, params object[] args)
        {
            using var scope = BeginScopeWithProperties();
            _logger.LogError(ex, messageTemplate, args);
        }

        private IDisposable BeginScopeWithProperties(params (string key, object value)[] properties)
        {
            var combinedScope = new Dictionary<string, object>(Scope);

            if (properties != null)
            {
                foreach (var (key, value) in properties)
                {
                    combinedScope[key] = SerializeScopeValue(value);
                }
            }

            return _logger.BeginScope(combinedScope);
        }

        private object SerializeScopeValue(object value)
        {
            if (value == null)
                return "null";

            if (IsSimple(value.GetType()))
                return value;

            try
            {
                return JsonSerializer.Serialize(value, new JsonSerializerOptions
                {
                    WriteIndented = false,
                    ReferenceHandler = ReferenceHandler.IgnoreCycles,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
            }
            catch
            {
                return $"[Unserializable: {value.GetType().FullName}]";
            }
        }

        //Note: unused for now, might need it in the future
        private string GetScopeValue(string key)
        {
            if(Scope.TryGetValue(key, out var value))
            {
                return value?.ToString();   
            }
            return null;
        }
        
        private void SetScopeValue(string key, object value) 
        {
            Scope[key] = value;
        }

        private static bool IsSimple(Type type)
        {
            return type.IsPrimitive ||
                   type.IsEnum ||
                   type == typeof(string) ||
                   type == typeof(decimal) ||
                   type == typeof(DateTime) ||
                   type == typeof(Guid) ||
                   type == typeof(DateTimeOffset);
        }
    }
}
