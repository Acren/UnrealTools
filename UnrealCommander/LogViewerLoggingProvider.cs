using System;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace UnrealCommander;

public class LogViewerLogger : ILogger
{
    public required LogViewer Viewer { get; set; }
    
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

                Viewer.Dispatcher.Invoke(() =>
                {
                    Viewer.WriteLog(formatter(state, exception) + exc, logLevel);
                });
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

public class LogViewerLoggingProvider : ILoggerProvider
{
    public LogViewer Viewer { get; set; }
    
    public void Dispose()
    {
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new LogViewerLogger() { Viewer = Viewer };
    }
}