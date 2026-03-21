using System;
using System.Threading.Tasks;
using LocalAutomation.Core;
using LocalAutomation.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
     /// Starts a shared execution session for the provided runtime operation.
     /// </summary>
    public LocalAutomation.Core.ExecutionSession StartExecution(Operation operation, OperationParameters parameters)
    {
        if (operation == null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        Runner runner = new(operation, parameters);
        BufferedLogStream logStream = new();
        ILogger previousLogger = TryGetCurrentLogger();
        ForwardingLogger forwardingLogger = new(logStream, previousLogger);
        ApplicationLogger.Logger = forwardingLogger;

        ExecutionSession? session = null;
        session = new ExecutionSession(logStream, cancelAsync: async () =>
        {
            if (runner.IsRunning)
            {
                await runner.Cancel();
                session!.IsRunning = false;
            }
        })
        {
            IsRunning = true,
            OperationName = operation.OperationName,
            TargetName = parameters.Target?.DisplayName ?? string.Empty
        };

        _ = RunAsync(runner, session, logStream, previousLogger);
        return session;
    }

    /// <summary>
    /// Runs the current runtime runner asynchronously and updates the shared execution session as it completes.
    /// </summary>
    private static async Task RunAsync(Runner runner, ExecutionSession session, BufferedLogStream logStream, ILogger previousLogger)
    {
        try
        {
            OperationResult result = await runner.Run();
            session.Success = result.Success;
        }
        catch (Exception ex)
        {
            logStream.Add(new LogEntry
            {
                Message = ex.ToString(),
                Verbosity = LogLevel.Error
            });

            session.Success = false;
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
    private sealed class ForwardingLogger : ILogger
    {
        private readonly BufferedLogStream _logStream;
        private readonly ILogger _fallbackLogger;

        /// <summary>
        /// Creates a forwarding logger around the provided buffered log stream.
        /// </summary>
        public ForwardingLogger(BufferedLogStream logStream, ILogger fallbackLogger)
        {
            _logStream = logStream;
            _fallbackLogger = fallbackLogger;
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

            _logStream.Add(new LogEntry
            {
                Message = message,
                Verbosity = logLevel
            });

            _fallbackLogger.Log(logLevel, eventId, state, exception, formatter);
        }

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
