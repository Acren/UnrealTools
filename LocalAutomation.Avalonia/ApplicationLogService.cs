using System;
using System.Threading.Tasks;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;
using Serilog;
using UnrealAutomationCommon;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace LocalAutomation.Avalonia;

/// <summary>
/// Owns the Avalonia shell's process-wide log stream so startup diagnostics, unhandled exceptions, and forwarded
/// runtime logs all flow into the same output panel even when no operation is currently executing.
/// </summary>
public static class ApplicationLogService
{
    private const int MaxLaunchLogFiles = 20;
    private const int MaxLogFileSizeBytes = 10 * 1024 * 1024;
    private const int MaxRollingLaunchLogFiles = 3;

    private static bool _isInitialized;
    private static ILoggerFactory? _loggerFactory;

    /// <summary>
    /// Gets the shared in-memory log stream rendered by the Avalonia shell.
    /// </summary>
    public static BufferedLogStream LogStream { get; } = new();

    /// <summary>
    /// Initializes the global AppLogger bridge and hooks process-wide exception reporting once per application run.
    /// </summary>
    public static void Initialize()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        string launchLogFilePath = ConfigureDiskLogging();
        BufferedLogger bufferedLogger = new(LogStream);
        _loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog(dispose: false));
        ILogger fileLogger = _loggerFactory.CreateLogger("LocalAutomation.Avalonia");
        AppLogger.Instance.Logger = new CompositeLogger(bufferedLogger, fileLogger);

        // Capture exceptions that escape normal async or UI flows so the output panel still shows the failure details
        // before the process tears down.
        AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
        TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;

        AppLogger.LoggerInstance.LogInformation("Writing launch log to {LaunchLogFilePath}", launchLogFilePath);
        AppLogger.LoggerInstance.LogInformation("Avalonia shell logging initialized.");
    }

    /// <summary>
    /// Flushes the file logger pipeline when the Avalonia app closes so the latest crash details land on disk.
    /// </summary>
    public static void Shutdown()
    {
        _loggerFactory?.Dispose();
        _loggerFactory = null;
        Log.CloseAndFlush();
    }

    /// <summary>
    /// Logs a startup failure that happens while the desktop lifetime is being created.
    /// </summary>
    public static void LogStartupException(Exception exception)
    {
        AppLogger.LoggerInstance.LogCritical(exception, "Avalonia shell failed during startup.");
    }

    /// <summary>
    /// Logs process-wide unhandled exceptions through the shared output stream.
    /// </summary>
    private static void HandleUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            AppLogger.LoggerInstance.LogCritical(exception, "Unhandled application exception.");
            return;
        }

        AppLogger.LoggerInstance.LogCritical("Unhandled non-exception application failure: {ExceptionObject}", args.ExceptionObject);
    }

    /// <summary>
    /// Logs unobserved task exceptions and marks them observed so the runtime does not hide the failure details.
    /// </summary>
    private static void HandleUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        AppLogger.LoggerInstance.LogCritical(args.Exception, "Unobserved task exception.");
        args.SetObserved();
    }

    /// <summary>
    /// Configures the Serilog file sink used for persistent launch logs and returns the active file path.
    /// </summary>
    private static string ConfigureDiskLogging()
    {
        LoggingPaths.CleanupOldLaunchLogs(MaxLaunchLogFiles);
        string launchLogFilePath = LoggingPaths.CreateLaunchLogFilePath();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.File(
                path: launchLogFilePath,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: MaxLogFileSizeBytes,
                retainedFileCountLimit: MaxRollingLaunchLogFiles,
                shared: true)
            .CreateLogger();

        return launchLogFilePath;
    }

    /// <summary>
    /// Writes formatted MEL log events into the shared buffered log stream used by the Avalonia output panel.
    /// </summary>
    private sealed class BufferedLogger : ILogger
    {
        private readonly BufferedLogStream _logStream;

        /// <summary>
        /// Creates a buffered logger for the provided in-memory log stream.
        /// </summary>
        public BufferedLogger(BufferedLogStream logStream)
        {
            _logStream = logStream;
        }

        /// <summary>
        /// Appends the rendered message and exception details to the shared output stream.
        /// </summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            if (exception != null)
            {
                message = string.IsNullOrWhiteSpace(message)
                    ? exception.ToString()
                    : message + Environment.NewLine + exception;
            }

            _logStream.Add(new LogEntry
            {
                Message = message,
                Verbosity = logLevel
            });
        }

        /// <summary>
        /// Keeps all log levels enabled because the UI log stream is the primary diagnostics surface for the shell.
        /// </summary>
        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        /// <summary>
        /// Returns a no-op scope because the buffered output stream does not currently model structured scopes.
        /// </summary>
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }
    }

    /// <summary>
    /// Mirrors each log entry to both the in-memory UI log stream and the file-backed logger pipeline.
    /// </summary>
    private sealed class CompositeLogger : ILogger
    {
        private readonly ILogger _bufferedLogger;
        private readonly ILogger _fileLogger;

        /// <summary>
        /// Creates a composite logger that fans out to both UI and file destinations.
        /// </summary>
        public CompositeLogger(ILogger bufferedLogger, ILogger fileLogger)
        {
            _bufferedLogger = bufferedLogger;
            _fileLogger = fileLogger;
        }

        /// <summary>
        /// Writes the same log entry to both backing loggers.
        /// </summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _bufferedLogger.Log(logLevel, eventId, state, exception, formatter);
            _fileLogger.Log(logLevel, eventId, state, exception, formatter);
        }

        /// <summary>
        /// Reports whether either backing logger is enabled for the requested level.
        /// </summary>
        public bool IsEnabled(LogLevel logLevel)
        {
            return _bufferedLogger.IsEnabled(logLevel) || _fileLogger.IsEnabled(logLevel);
        }

        /// <summary>
        /// Returns a composite scope that keeps both backing logger scopes alive for the same operation.
        /// </summary>
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return new ScopePair(_bufferedLogger.BeginScope(state) ?? NullScope.Instance, _fileLogger.BeginScope(state) ?? NullScope.Instance);
        }
    }

    /// <summary>
    /// Disposes paired scopes created by the composite logger.
    /// </summary>
    private sealed class ScopePair : IDisposable
    {
        private readonly IDisposable _first;
        private readonly IDisposable _second;

        /// <summary>
        /// Creates a scope wrapper for two underlying logging scopes.
        /// </summary>
        public ScopePair(IDisposable first, IDisposable second)
        {
            _first = first;
            _second = second;
        }

        /// <summary>
        /// Disposes both underlying scopes.
        /// </summary>
        public void Dispose()
        {
            _second.Dispose();
            _first.Dispose();
        }
    }

    /// <summary>
    /// Provides a shared no-op scope instance for log calls that request scoped logging.
    /// </summary>
    private sealed class NullScope : IDisposable
    {
        /// <summary>
        /// Gets the singleton no-op scope instance.
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
