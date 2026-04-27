using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LocalAutomation.Runtime;

/// <summary>
/// Provides one process-wide arbitration point for typed execution locks. Operations declare logical lock needs, and the
/// scheduler registers ready tasks here so each acquisition attempt can choose the best currently eligible waiter.
/// </summary>
internal static class ExecutionLocks
{
    // Protects the held-key set and waiter list so lock grants are chosen from one coherent global view.
    private static readonly object _syncRoot = new();

    // Tracks lock keys currently owned by granted task executions.
    private static readonly HashSet<string> _heldKeys = new(StringComparer.Ordinal);

    // Stores tasks that are eligible to run except for their declared lock set.
    private static readonly List<LockWaiter> _waiters = new();

    // Gives equal-priority waiters a deterministic global FIFO tie-breaker.
    private static long _nextWaiterSequence;

    /// <summary>
    /// Fires after a lock-table change may let waiting schedulers retry acquisition.
    /// </summary>
    internal static event Action? Changed;

    /// <summary>
    /// Provides one no-op lock handle for tasks that do not declare any lock requirements.
    /// </summary>
    internal static IAsyncDisposable EmptyHandle => new Releaser(Array.Empty<string>());

    /// <summary>
    /// Registers or refreshes one scheduler-ready task and acquires its locks only when it is the best eligible waiter.
    /// </summary>
    internal static bool TryAcquireOrWait(
        ExecutionTaskId taskId,
        IEnumerable<ExecutionLock> executionLocks,
        int priority,
        out IAsyncDisposable handle)
    {
        handle = EmptyHandle;
        if (taskId == default)
        {
            throw new ArgumentException("A task id is required for lock wait registration.", nameof(taskId));
        }

        if (executionLocks == null)
        {
            throw new ArgumentNullException(nameof(executionLocks));
        }

        IReadOnlyList<string> keys = GetOrderedKeys(executionLocks);
        if (keys.Count == 0)
        {
            return true;
        }

        bool wakeWaiters = false;
        lock (_syncRoot)
        {
            int waiterIndex = _waiters.FindIndex(waiter => waiter.TaskId == taskId);
            long sequence = waiterIndex >= 0
                ? _waiters[waiterIndex].Sequence
                : Interlocked.Increment(ref _nextWaiterSequence);
            LockWaiter waiter = new(taskId, keys, priority, sequence);
            if (waiterIndex >= 0)
            {
                _waiters[waiterIndex] = waiter;
            }
            else
            {
                _waiters.Add(waiter);
            }

            LockWaiter? bestWaiter = _waiters
                .Where(candidate => ConflictsWith(candidate.Keys, keys) && candidate.Keys.All(key => !_heldKeys.Contains(key)))
                .OrderByDescending(candidate => candidate.Priority)
                .ThenBy(candidate => candidate.Sequence)
                .Select(candidate => (LockWaiter?)candidate)
                .FirstOrDefault();
            if (bestWaiter?.TaskId == taskId)
            {
                _waiters.RemoveAll(candidate => candidate.TaskId == taskId);
                foreach (string key in keys)
                {
                    _heldKeys.Add(key);
                }

                handle = new Releaser(keys);
                return true;
            }

            wakeWaiters = bestWaiter != null;
        }

        if (wakeWaiters)
        {
            SignalChanged();
        }

        return false;
    }

    /// <summary>
    /// Removes one queued task from global lock arbitration.
    /// </summary>
    internal static void UnregisterWaiter(ExecutionTaskId taskId)
    {
        bool removed;
        lock (_syncRoot)
        {
            removed = _waiters.RemoveAll(waiter => waiter.TaskId == taskId) > 0;
        }

        if (removed)
        {
            SignalChanged();
        }
    }

    /// <summary>
    /// Normalizes the declared locks into one deterministic ordered key list for conflict checks and release symmetry.
    /// </summary>
    private static IReadOnlyList<string> GetOrderedKeys(IEnumerable<ExecutionLock> executionLocks)
    {
        /* Lock keys are sorted before acquisition so multi-lock tasks always record and release the same normalized set,
           regardless of the order in which operations declared those locks. */
        return executionLocks
            .Select(executionLock => executionLock.Key)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Returns whether two normalized lock sets overlap on any key.
    /// </summary>
    private static bool ConflictsWith(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        return left.Any(right.Contains);
    }

    /// <summary>
    /// Releases one granted lock set and wakes all known waiters so their schedulers can retry acquisition.
    /// </summary>
    private static void Release(IReadOnlyList<string> keys)
    {
        lock (_syncRoot)
        {
            foreach (string key in keys)
            {
                _heldKeys.Remove(key);
            }
        }

        SignalChanged();
    }

    /// <summary>
    /// Notifies active schedulers outside the lock-table monitor so they can retry ready lock waiters.
    /// </summary>
    private static void SignalChanged()
    {
        Changed?.Invoke();
    }

    /// <summary>
    /// Owns one acquired lock-key set and returns those keys to the global coordinator when disposed.
    /// </summary>
    private sealed class Releaser(IReadOnlyList<string> keys) : IAsyncDisposable
    {
        private int _disposed;

        /// <summary>
        /// Releases the granted keys once, skipping notification for the shared lock-free path.
        /// </summary>
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return default;
            }

            if (keys.Count > 0)
            {
                Release(keys);
            }

            return default;
        }
    }

    /// <summary>
    /// Represents one scheduler-ready task that is blocked only by its requested execution locks.
    /// </summary>
    private readonly record struct LockWaiter(ExecutionTaskId TaskId, IReadOnlyList<string> Keys, int Priority, long Sequence);

}
