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
        bool acquired = TryAcquireOrWait(
            taskId,
            new[] { (executionLocks ?? throw new ArgumentNullException(nameof(executionLocks))).ToList() },
            priority,
            out IReadOnlyList<IAsyncDisposable> handles);
        handle = handles.Count > 0 ? handles[0] : EmptyHandle;
        return acquired;
    }

    /// <summary>
    /// Registers or refreshes one scheduler-ready task and atomically acquires all requested lock groups only when that
    /// ready task is the best eligible waiter. Each group receives its own release handle so a task-scope lock can be
    /// released when its owning task completes without holding unrelated inner task locks for too long.
    /// </summary>
    internal static bool TryAcquireOrWait(
        ExecutionTaskId taskId,
        IReadOnlyList<IReadOnlyList<ExecutionLock>> executionLockGroups,
        int priority,
        out IReadOnlyList<IAsyncDisposable> handles)
    {
        handles = Array.Empty<IAsyncDisposable>();
        if (taskId == default)
        {
            throw new ArgumentException("A task id is required for lock wait registration.", nameof(taskId));
        }

        if (executionLockGroups == null)
        {
            throw new ArgumentNullException(nameof(executionLockGroups));
        }

        IReadOnlyList<IReadOnlyList<string>> keyGroups = GetOrderedKeyGroups(executionLockGroups);
        IReadOnlyList<string> keys = keyGroups
            .SelectMany(group => group)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
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

                handles = keyGroups.Select(group => (IAsyncDisposable)new Releaser(group)).ToList();
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
    /// Releases one previously granted lock handle through the same synchronous boundary the scheduler already uses for
    /// failed task admission. Lock handles only mutate the in-process lock table, so blocking here keeps release ordering
    /// deterministic without introducing fire-and-forget cleanup.
    /// </summary>
    internal static void ReleaseHandle(IAsyncDisposable handle)
    {
        _ = handle ?? throw new ArgumentNullException(nameof(handle));
        handle.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
    /// Normalizes grouped lock declarations into deterministic key sets and rejects duplicate ownership of one key inside
    /// the same atomic acquisition because each granted key must have exactly one release handle.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<string>> GetOrderedKeyGroups(IReadOnlyList<IReadOnlyList<ExecutionLock>> executionLockGroups)
    {
        HashSet<string> assignedKeys = new(StringComparer.Ordinal);
        List<IReadOnlyList<string>> keyGroups = new();
        foreach (IReadOnlyList<ExecutionLock> executionLockGroup in executionLockGroups)
        {
            IReadOnlyList<string> groupKeys = GetOrderedKeys(executionLockGroup);
            if (groupKeys.Count == 0)
            {
                continue;
            }

            foreach (string key in groupKeys)
            {
                if (!assignedKeys.Add(key))
                {
                    throw new InvalidOperationException($"Execution lock key '{key}' cannot be assigned to more than one release scope in the same acquisition.");
                }
            }

            keyGroups.Add(groupKeys);
        }

        return keyGroups;
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
