using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LocalAutomation.Core;
using LocalAutomation.Runtime;
using Microsoft.Extensions.Logging;
using TestUtilities;

namespace LocalAutomation.Application.Tests;

internal static class ApplicationTestUtilities
{
    /// <summary>
    /// Captures application-log writes in memory and exposes a waiter for the first error entry so tests can observe
    /// background failures deterministically.
    /// </summary>
    internal sealed class CapturingLogger : ILogger
    {
        private readonly object _syncRoot = new();
        private readonly List<CapturedLogEntry> _entries = new();
        private readonly TaskCompletionSource<CapturedLogEntry> _firstErrorSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<CapturedLogEntry> Entries
        {
            get
            {
                lock (_syncRoot)
                {
                    return _entries.ToArray();
                }
            }
        }

        /// <summary>
        /// Waits for the first error-or-higher log entry recorded by this logger.
        /// </summary>
        public Task<CapturedLogEntry> WaitForErrorAsync()
        {
            return _firstErrorSource.Task;
        }

        /// <summary>
        /// Waits for the first captured error entry whose message satisfies the provided predicate.
        /// </summary>
        public async Task<CapturedLogEntry> WaitForErrorAsync(Func<CapturedLogEntry, bool> predicate, TimeSpan timeout)
        {
            if (predicate == null)
            {
                throw new ArgumentNullException(nameof(predicate));
            }

            CapturedLogEntry? existingMatch;
            lock (_syncRoot)
            {
                existingMatch = _entries.FirstOrDefault(entry => entry.Level >= LogLevel.Error && predicate(entry));
            }

            if (existingMatch != null)
            {
                return existingMatch;
            }

            TaskCompletionSource<CapturedLogEntry> matchSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnEntryCaptured(CapturedLogEntry entry)
            {
                if (entry.Level >= LogLevel.Error && predicate(entry))
                {
                    matchSource.TrySetResult(entry);
                }
            }

            EntryCaptured += OnEntryCaptured;
            try
            {
                return await matchSource.Task.WaitAsync(timeout);
            }
            finally
            {
                EntryCaptured -= OnEntryCaptured;
            }
        }

        /// <summary>
        /// Application tests enable all levels because they only care about whether the expected error was forwarded.
        /// </summary>
        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        /// <summary>
        /// Test logging does not require scope tracking, so a no-op disposable keeps the implementation minimal.
        /// </summary>
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        /// <summary>
        /// Captures the formatted message plus exception payload so tests can assert that background execution failures are
        /// routed into the application logger with the original exception attached.
        /// </summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter == null)
            {
                throw new ArgumentNullException(nameof(formatter));
            }

            CapturedLogEntry entry = new(logLevel, formatter(state, exception), exception);
            lock (_syncRoot)
            {
                _entries.Add(entry);
            }

            EntryCaptured?.Invoke(entry);

            if (logLevel >= LogLevel.Error)
            {
                _firstErrorSource.TrySetResult(entry);
            }
        }

        private event Action<CapturedLogEntry>? EntryCaptured;

        internal sealed record CapturedLogEntry(LogLevel Level, string Message, Exception? Exception);

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            /// <summary>
            /// No-op dispose for logger scopes that exist only to satisfy the ILogger contract in tests.
            /// </summary>
            public void Dispose()
            {
            }
        }
    }
}
