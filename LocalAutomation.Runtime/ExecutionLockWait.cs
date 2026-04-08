using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Runtime;

/// <summary>
/// Implements the task-execution side of waiting on declared execution locks. Scheduler admission decides which task is
/// allowed to contend next, while this helper owns the actual wait and paired lock acquisition/release logging.
/// </summary>
internal static class ExecutionLockWait
{
    /// <summary>
    /// Acquires one task body's declared execution locks and returns a handle that will log the eventual release when the
    /// task execution path unwinds.
    /// </summary>
    internal static async Task<IAsyncDisposable> AcquireAsync(ExecutionTask task, IReadOnlyList<ExecutionLock> executionLocks, ILogger taskLogger, CancellationToken cancellationToken)
    {
        if (task == null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        if (executionLocks == null)
        {
            throw new ArgumentNullException(nameof(executionLocks));
        }

        if (taskLogger == null)
        {
            throw new ArgumentNullException(nameof(taskLogger));
        }

        if (executionLocks.Count == 0)
        {
            return EmptyAsyncDisposable.Instance;
        }

        string lockSummary = string.Join(", ", executionLocks.Select(executionLock => executionLock.Key));
        taskLogger.LogDebug("Waiting for execution lock(s) for task '{TaskTitle}': {ExecutionLocks}", task.Title, lockSummary);
        IAsyncDisposable handle = await ExecutionLocks.AcquireAsync(executionLocks, cancellationToken).ConfigureAwait(false);
        taskLogger.LogInformation("Acquired execution lock(s): {ExecutionLocks}", lockSummary);
        return new LoggedLockHandle(handle, taskLogger, lockSummary);
    }

    /// <summary>
    /// Provides one shared no-op async disposable so task execution can use a single await-using path even when no locks
    /// were declared.
    /// </summary>
    private sealed class EmptyAsyncDisposable : IAsyncDisposable
    {
        public static EmptyAsyncDisposable Instance { get; } = new();

        public ValueTask DisposeAsync() => default;
    }

    /// <summary>
    /// Wraps one acquired execution-lock handle so acquisition and release logs stay paired to the owning task.
    /// </summary>
    private sealed class LoggedLockHandle : IAsyncDisposable
    {
        private readonly IAsyncDisposable _inner;
        private readonly ILogger _logger;
        private readonly string _summary;

        public LoggedLockHandle(IAsyncDisposable inner, ILogger logger, string summary)
        {
            _inner = inner;
            _logger = logger;
            _summary = summary;
        }

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            _logger.LogInformation("Released execution lock(s): {ExecutionLocks}", _summary);
        }
    }
}
