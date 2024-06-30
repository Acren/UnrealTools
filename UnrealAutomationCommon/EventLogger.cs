using System;
using Microsoft.Extensions.Logging;

namespace UnrealAutomationCommon;

public class EventLogger : ILogger
{
    public delegate void LogEventHandler(LogLevel level, string output);

    public event LogEventHandler Output;
    
    private static object _lock = new object();
    
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        if (formatter != null)
        {
            lock (_lock)
            {
                var n = Environment.NewLine;
                string exc = "";
                if (exception != null)
                {
                    exc = n + exception.GetType() + ": " + exception.Message + n + exception.StackTrace + n;
                }

                if (Output != null)
                {
                    Output.Invoke(logLevel, formatter(state, exception) + exc);
                }
            }
        }
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }
}