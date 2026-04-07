using System;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;

namespace TestUtilities;

/// <summary>
/// Builds one shared MEL logger pipeline for test assemblies so they can initialize process-wide logging without per-test
/// wiring.
/// </summary>
public static class TestLoggingBootstrap
{
    private static readonly object SyncRoot = new();
    private static ILoggerFactory? _loggerFactory;
    private static bool _isInitialized;

    /// <summary>
    /// Gets the shared test logger factory, initializing it on first access.
    /// </summary>
    public static ILoggerFactory LoggerFactory
    {
        get
        {
            EnsureInitialized();
            return _loggerFactory!;
        }
    }

    private static void EnsureInitialized()
    {
        lock (SyncRoot)
        {
            if (_isInitialized)
            {
                return;
            }

            _loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Debug);
                builder.AddConsole();
            });

            ILogger rootLogger = _loggerFactory.CreateLogger("TestHost");
            ApplicationLogger.Logger = rootLogger;
            _isInitialized = true;
        }
    }
}
