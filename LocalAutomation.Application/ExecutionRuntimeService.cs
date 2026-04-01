using System;
using System.Threading.Tasks;
using LocalAutomation.Core;
using LocalAutomation.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RuntimeExecutionTaskId = LocalAutomation.Runtime.ExecutionTaskId;
using RuntimeExecutionTaskStatus = LocalAutomation.Runtime.ExecutionTaskStatus;
using RuntimeExecutionRunOutcome = LocalAutomation.Runtime.RunOutcome;

namespace LocalAutomation.Application;

/// <summary>
/// Starts shared execution sessions for runtime-typed operations.
/// </summary>
public sealed class ExecutionRuntimeService
{
    /// <summary>
    /// Creates an execution runtime service.
    /// </summary>
    public ExecutionRuntimeService()
    {
    }

    /// <summary>
     /// Starts a shared execution session for the provided runtime operation. Callers can supply a session-created hook
     /// so UI or host services attach listeners before execution begins and early task-state transitions are not lost.
     /// </summary>
    public LocalAutomation.Runtime.ExecutionSession StartExecution(Operation operation, OperationParameters parameters, Action<LocalAutomation.Runtime.ExecutionSession>? onSessionCreated = null)
    {
        if (operation == null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        Runner runner = new(operation, parameters);
        BufferedLogStream logStream = new();
        LocalAutomation.Runtime.ExecutionPlan plan = runner.BuildPlan()
            ?? throw new InvalidOperationException($"Operation '{operation.OperationName}' did not produce an execution plan.");
        ILogger previousLogger = TryGetCurrentLogger();
        LocalAutomation.Runtime.ExecutionSession? session = null;
        ForwardingLogger forwardingLogger = new(() => session!, previousLogger);
        ApplicationLogger.Logger = forwardingLogger;

        session = new LocalAutomation.Runtime.ExecutionSession(logStream, cancelAsync: async () =>
        {
            if (runner.IsRunning)
            {
                await runner.Cancel();
                session!.IsRunning = false;
            }
        }, plan: plan)
        {
            IsRunning = true,
            OperationName = operation.OperationName,
            TargetName = parameters.Target?.DisplayName ?? string.Empty
        };

        /* Copying the preview plan into the session preserves the authored graph structure, but the live execution tab
           should start from runtime-ready state instead of continuing to show preview-only Planned nodes. */
        session.BeginExecution();

        // Let UI consumers subscribe to task-status and task-log streams before the runner starts so the first Running
        // transition for long-lived tasks is visible on the graph instead of being lost during startup.
        try
        {
            onSessionCreated?.Invoke(session);
        }
        catch
        {
            ApplicationLogger.Logger = previousLogger;
            throw;
        }

        _ = RunAsync(runner, session, plan, previousLogger);
        return session;
    }

    /// <summary>
    /// Runs the current runtime runner asynchronously and updates the shared execution session as it completes.
    /// </summary>
    private static async Task RunAsync(Runner runner, LocalAutomation.Runtime.ExecutionSession session, LocalAutomation.Runtime.ExecutionPlan plan, ILogger previousLogger)
    {
        try
        {
            OperationResult result = await runner.Run(plan);
            session.Outcome = result.Outcome;
        }
        catch (OperationCanceledException)
        {
            session.Outcome = RuntimeExecutionRunOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            session.AddLogEntry(new LogEntry
            {
                SessionId = session.Id.Value,
                Message = ex.ToString(),
                Verbosity = LogLevel.Error
            });

            session.Outcome = RuntimeExecutionRunOutcome.Failed;
        }
        finally
        {
            session.IsRunning = false;
            ApplicationLogger.Logger = previousLogger;
        }
    }

    /// <summary>
    /// Returns the current application logger when one has been initialized, otherwise falls back to a null logger.
    /// </summary>
    private static ILogger TryGetCurrentLogger()
    {
        try
        {
            return ApplicationLogger.Logger;
        }
        catch (InvalidOperationException)
        {
            return NullLogger.Instance;
        }
    }

    /// <summary>
    /// Forwards shared logger output into the execution session's buffered log stream.
    /// </summary>
    private sealed class ForwardingLogger : ILogger, LocalAutomation.Runtime.IExecutionTaskLoggerFactory, LocalAutomation.Runtime.IExecutionTaskStateSink, LocalAutomation.Runtime.IExecutionTaskScope
    {
        private readonly Func<LocalAutomation.Runtime.ExecutionSession> _getSession;
        private readonly ILogger _fallbackLogger;
        private readonly RuntimeExecutionTaskId? _taskId;

        /// <summary>
        /// Creates a forwarding logger around the provided buffered log stream.
        /// </summary>
        public ForwardingLogger(Func<LocalAutomation.Runtime.ExecutionSession> getSession, ILogger fallbackLogger, RuntimeExecutionTaskId? taskId = null)
        {
            _getSession = getSession;
            _fallbackLogger = fallbackLogger;
            _taskId = taskId;
        }

        /// <summary>
        /// Appends formatted log messages to the buffered log stream.
        /// </summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            if (exception != null)
            {
                message += Environment.NewLine + exception;
            }

            LocalAutomation.Runtime.ExecutionSession session = _getSession();
            session.AddLogEntry(new LogEntry
            {
                SessionId = session.Id.Value,
                TaskId = _taskId?.Value,
                Message = message,
                Verbosity = logLevel
            });

            _fallbackLogger.Log(logLevel, eventId, state, exception, formatter);
        }

        /// <summary>
        /// Gets the current task scope carried by this logger so nested operations can inherit the same task identity.
        /// </summary>
        public RuntimeExecutionTaskId? CurrentTaskId => _taskId;

        /// <summary>
        /// Indicates that all log levels are enabled for the buffered execution stream.
        /// </summary>
        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        /// <summary>
        /// Returns a no-op scope because the buffered stream does not track structured scope state.
        /// </summary>
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return _fallbackLogger.BeginScope(state) ?? NullScope.Instance;
        }

        /// <summary>
        /// Creates a child logger that attributes all output to the provided execution task.
        /// </summary>
        public ILogger CreateTaskLogger(RuntimeExecutionTaskId taskId)
        {
            return new ForwardingLogger(_getSession, _fallbackLogger, taskId);
        }

        /// <summary>
        /// Forwards explicit task-state transitions into the current execution session so graph views can react without
        /// parsing human-readable log lines.
        /// </summary>
        public void SetTaskStatus(RuntimeExecutionTaskId taskId, RuntimeExecutionTaskStatus status, string? statusReason = null)
        {
            _getSession().SetTaskStatus(taskId, status, statusReason);
        }

        /// <summary>
        /// Provides a no-op scope object when the fallback logger does not create real scopes.
        /// </summary>
        private sealed class NullScope : IDisposable
        {
            /// <summary>
            /// Gets the shared no-op scope instance.
            /// </summary>
            public static NullScope Instance { get; } = new();

            /// <summary>
            /// Disposes the no-op scope.
            /// </summary>
            public void Dispose()
            {
            }
        }
    }
}
