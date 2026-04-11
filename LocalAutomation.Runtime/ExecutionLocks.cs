using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LocalAutomation.Runtime;

/// <summary>
/// Provides in-process exclusive locks for typed execution locks. Operations declare logical lock needs and
/// the runtime translates them into shared semaphores so concurrent tasks in the same app instance cannot race on shared
/// external resources.
/// </summary>
public static class ExecutionLocks
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>
    /// Tries to acquire all provided execution locks immediately and returns one releaser that frees them in reverse
    /// order. The scheduler uses this to keep untouched lock contenders queued until the callback can truly start.
    /// </summary>
    public static bool TryAcquire(IEnumerable<ExecutionLock> executionLocks, out IAsyncDisposable handle)
    {
        if (executionLocks == null)
        {
            throw new ArgumentNullException(nameof(executionLocks));
        }

        IReadOnlyList<string> keys = GetOrderedKeys(executionLocks);
        if (keys.Count == 0)
        {
            handle = AsyncDisposable.Empty;
            return true;
        }

        List<(string Key, SemaphoreSlim Semaphore)> acquiredLocks = new(keys.Count);
        try
        {
            foreach (string key in keys)
            {
                SemaphoreSlim semaphore = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
                if (!semaphore.Wait(0))
                {
                    ReleaseAcquiredLocks(acquiredLocks);
                    handle = AsyncDisposable.Empty;
                    return false;
                }

                acquiredLocks.Add((key, semaphore));
            }

            handle = new Releaser(acquiredLocks);
            return true;
        }
        catch
        {
            ReleaseAcquiredLocks(acquiredLocks);
            handle = AsyncDisposable.Empty;
            throw;
        }
    }

    /// <summary>
    /// Acquires all provided execution locks asynchronously in a deterministic order and returns one releaser that frees
    /// them in reverse order.
    /// </summary>
    public static async Task<IAsyncDisposable> AcquireAsync(IEnumerable<ExecutionLock> executionLocks, CancellationToken cancellationToken)
    {
        if (executionLocks == null)
        {
            throw new ArgumentNullException(nameof(executionLocks));
        }

        IReadOnlyList<string> keys = GetOrderedKeys(executionLocks);
        if (keys.Count == 0)
        {
            return AsyncDisposable.Empty;
        }

        List<(string Key, SemaphoreSlim Semaphore)> acquiredLocks = new(keys.Count);
        try
        {
            foreach (string key in keys)
            {
                SemaphoreSlim semaphore = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquiredLocks.Add((key, semaphore));
            }

            return new Releaser(acquiredLocks);
        }
        catch
        {
            ReleaseAcquiredLocks(acquiredLocks);
            throw;
        }
    }

    /// <summary>
    /// Normalizes the declared locks into one deterministic ordered key list so both blocking and non-blocking acquire
    /// paths use the same deadlock-free ordering.
    /// </summary>
    private static IReadOnlyList<string> GetOrderedKeys(IEnumerable<ExecutionLock> executionLocks)
    {
        /* Lock keys are sorted before acquisition so any task body that needs more than one lock waits in the same order
           as every other task body. That keeps the in-process lock table deadlock-free even when different operations
           declare overlapping lock sets. */
        return executionLocks
            .Select(executionLock => executionLock.Key)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Releases a partially acquired lock set in reverse order so failed multi-lock attempts do not leak semaphore state.
    /// </summary>
    private static void ReleaseAcquiredLocks(IReadOnlyList<(string Key, SemaphoreSlim Semaphore)> acquiredLocks)
    {
        for (int index = acquiredLocks.Count - 1; index >= 0; index--)
        {
            acquiredLocks[index].Semaphore.Release();
        }
    }

    private sealed class Releaser : IAsyncDisposable
    {
        private readonly IReadOnlyList<(string Key, SemaphoreSlim Semaphore)> _acquiredLocks;
        private int _disposed;

        public Releaser(IReadOnlyList<(string Key, SemaphoreSlim Semaphore)> acquiredLocks)
        {
            _acquiredLocks = acquiredLocks;
        }

        /// <summary>
        /// Releases the acquired semaphores in reverse acquisition order so stacked lock scopes unwind symmetrically with
        /// the scheduler's deterministic acquisition order.
        /// </summary>
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return default;
            }

            for (int index = _acquiredLocks.Count - 1; index >= 0; index--)
            {
                _acquiredLocks[index].Semaphore.Release();
            }

            return default;
        }
    }

    private sealed class AsyncDisposable : IAsyncDisposable
    {
        public static AsyncDisposable Empty { get; } = new();

        /// <summary>
        /// Provides one no-op lock handle for task bodies that do not declare any lock requirements so the scheduler can use
        /// a single disposal path regardless of whether locks were acquired.
        /// </summary>
        public ValueTask DisposeAsync() => default;
    }
}
