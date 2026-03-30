using System;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Core;

/// <summary>
/// Implements <see cref="ILogger"/> by forwarding formatted log output through an event stream that callers can
/// subscribe to.
/// </summary>
public class EventStreamLogger : ILogger, IExecutionTaskLoggerFactory, IExecutionTaskStateSink
{
    private static readonly object LockObject = new();
    private readonly IExecutionTaskLoggerFactory? _taskLoggerFactory;
    private readonly IExecutionTaskStateSink? _taskStateSink;

    /// <summary>
    /// Creates an event-stream logger that can optionally delegate task-specific logging and state transitions to an
    /// outer execution-aware logger.
    /// </summary>
    public EventStreamLogger(IExecutionTaskLoggerFactory? taskLoggerFactory = null, IExecutionTaskStateSink? taskStateSink = null)
    {
        _taskLoggerFactory = taskLoggerFactory;
        _taskStateSink = taskStateSink;
    }

    /// <summary>
    /// Raised whenever a formatted log message is emitted.
    /// </summary>
    public event Action<LogLevel, string>? Output;

    /// <summary>
    /// Formats and forwards each log message to subscribers, including exception details when provided.
    /// </summary>
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

    /// <summary>
    /// Indicates that the logger accepts all log levels.
    /// </summary>
    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    /// <summary>
    /// This logger does not maintain scoped state, so it returns a no-op disposable.
    /// </summary>
    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        return NullScope.Instance;
    }

    /// <summary>
    /// Creates a logger for one execution-plan task when an outer execution-aware logger is available.
    /// </summary>
    public ILogger CreateTaskLogger(ExecutionTaskId taskId)
    {
        return _taskLoggerFactory?.CreateTaskLogger(taskId) ?? this;
    }

    /// <summary>
    /// Forwards task-state transitions to the outer execution-aware logger when available.
    /// </summary>
    public void SetTaskStatus(ExecutionTaskId taskId, ExecutionTaskStatus status, string? statusReason = null)
    {
        _taskStateSink?.SetTaskStatus(taskId, status, statusReason);
    }

    /// <summary>
    /// Provides a reusable disposable for callers that request logging scopes.
    /// </summary>
    private sealed class NullScope : IDisposable
    {
        /// <summary>
        /// Reuses a single no-op scope instance for all calls.
        /// </summary>
        public static NullScope Instance { get; } = new();

        /// <summary>
        /// Performs no work because event-based logging does not track scope lifetime.
        /// </summary>
        public void Dispose()
        {
        }
    }
}
