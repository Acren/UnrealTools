using System;
using System.Threading.Tasks;
using LocalAutomation.Core;
using LocalAutomation.Extensions.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.BaseOperations;

namespace LocalAutomation.Extensions.Unreal;

/// <summary>
/// Bridges the existing UnrealAutomationCommon runner into the shared LocalAutomation execution session model.
/// </summary>
public sealed class UnrealRunnerAdapter : IRunnerAdapter
{
    /// <summary>
    /// Gets the stable identifier for the Unreal runner adapter.
    /// </summary>
    public string Id => "unreal.runner-adapter";

    /// <summary>
    /// Returns whether the provided runtime operation belongs to the current Unreal operation hierarchy.
    /// </summary>
    public bool CanHandle(object operation)
    {
        return operation is Operation;
    }

    /// <summary>
    /// Starts a shared execution session that mirrors the existing Unreal runner lifecycle and forwards log output
    /// into a shared in-memory buffer for UI hosts.
    /// </summary>
    public ExecutionSession StartExecution(object operation, object parameters)
    {
        if (operation is not Operation typedOperation)
        {
            throw new ArgumentException("Operation is not a supported Unreal operation", nameof(operation));
        }

        if (parameters is not OperationParameters typedParameters)
        {
            throw new ArgumentException("Parameters are not Unreal operation parameters", nameof(parameters));
        }

        BufferedLogStream logStream = new();
        ILogger previousLogger = TryGetCurrentLogger();
        ILogger previousLegacyLogger = TryGetCurrentLegacyLogger(previousLogger);
        ForwardingLogger forwardingLogger = new(logStream, previousLogger);
        ApplicationLogger.Logger = forwardingLogger;
        AppLogger.Instance.Logger = forwardingLogger;
        Runner runner = new(typedOperation, typedParameters);

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
            OperationName = typedOperation.OperationName,
            TargetName = typedParameters.Target?.DisplayName ?? string.Empty
        };

        _ = RunAsync(runner, session, logStream, previousLogger, previousLegacyLogger);
        return session;
    }

    /// <summary>
    /// Runs the existing Unreal runner asynchronously and updates the shared execution session as it completes.
    /// </summary>
    private static async Task RunAsync(Runner runner, ExecutionSession session, BufferedLogStream logStream, ILogger previousLogger, ILogger previousLegacyLogger)
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
            AppLogger.Instance.Logger = previousLegacyLogger;
            ApplicationLogger.Logger = previousLogger;
        }
    }

    /// <summary>
    /// Returns the current application logger when one has been initialized, otherwise falls back to a null logger so
    /// headless or test hosts can still execute Unreal operations safely.
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
    /// Returns the current legacy Unreal logger when one has already been bridged, otherwise falls back to the current
    /// shared application logger so both logger singletons can be restored consistently after the task finishes.
    /// </summary>
    private static ILogger TryGetCurrentLegacyLogger(ILogger fallbackLogger)
    {
        return AppLogger.Instance.Logger ?? fallbackLogger;
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
            return _fallbackLogger.BeginScope(state);
        }
    }
}
