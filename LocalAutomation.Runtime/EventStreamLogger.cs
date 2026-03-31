using System;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Runtime;

/// <summary>
/// Implements <see cref="ILogger"/> by forwarding formatted log output through an event stream that callers can subscribe to.
/// </summary>
public class EventStreamLogger : ILogger, IExecutionTaskLoggerFactory, IExecutionTaskStateSink
{
    private static readonly object LockObject = new();
    private readonly IExecutionTaskLoggerFactory? _taskLoggerFactory;
    private readonly IExecutionTaskStateSink? _taskStateSink;

    public EventStreamLogger(IExecutionTaskLoggerFactory? taskLoggerFactory = null, IExecutionTaskStateSink? taskStateSink = null)
    {
        _taskLoggerFactory = taskLoggerFactory;
        _taskStateSink = taskStateSink;
    }

    public event Action<LogLevel, string>? Output;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (formatter == null)
        {
            return;
        }

        lock (LockObject)
        {
            string exceptionText = string.Empty;
            if (exception != null)
            {
                string newLine = Environment.NewLine;
                exceptionText = newLine + exception.GetType() + ": " + exception.Message + newLine + exception.StackTrace + newLine;
            }

            Output?.Invoke(logLevel, formatter(state, exception) + exceptionText);
        }
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        return NullScope.Instance;
    }

    public ILogger CreateTaskLogger(ExecutionTaskId taskId)
    {
        return _taskLoggerFactory?.CreateTaskLogger(taskId) ?? this;
    }

    public void SetTaskStatus(ExecutionTaskId taskId, ExecutionTaskStatus status, string? statusReason = null)
    {
        _taskStateSink?.SetTaskStatus(taskId, status, statusReason);
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
