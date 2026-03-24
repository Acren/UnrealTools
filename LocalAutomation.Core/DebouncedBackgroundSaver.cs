using System;
using System.Threading;
using System.Threading.Tasks;

namespace LocalAutomation.Core;

/// <summary>
/// Coalesces repeated save requests into one debounced background write so callers can capture detached state on the
/// UI thread without blocking on file I/O for every edit.
/// </summary>
public sealed class DebouncedBackgroundSaver<TState> : IDisposable where TState : class
{
    private readonly TimeSpan _debounceDelay;
    private readonly Action<Exception>? _handleSaveException;
    private readonly Func<TState, TState, TState> _mergeStates;
    private readonly Action<TState> _saveState;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);

    private CancellationTokenSource? _delayCancellationSource;
    private bool _disposed;
    private bool _hasPendingState;
    private TState? _pendingState;

    /// <summary>
    /// Creates a debounced background saver around one payload writer and an optional merge policy for queued edits.
    /// </summary>
    public DebouncedBackgroundSaver(
        TimeSpan debounceDelay,
        Action<TState> saveState,
        Func<TState, TState, TState>? mergeStates = null,
        Action<Exception>? handleSaveException = null)
    {
        if (debounceDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounceDelay), "Debounce delay must be zero or positive.");
        }

        _debounceDelay = debounceDelay;
        _saveState = saveState ?? throw new ArgumentNullException(nameof(saveState));
        _mergeStates = mergeStates ?? ReplacePendingState;
        _handleSaveException = handleSaveException;
    }

    /// <summary>
    /// Queues one captured payload for saving after the debounce window elapses.
    /// </summary>
    public void RequestSave(TState state)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        QueueState(state, scheduleDelay: true);
    }

    /// <summary>
    /// Saves the latest pending payload immediately and optionally folds in one final captured payload before writing.
    /// </summary>
    public void Flush(TState? latestState = null)
    {
        FlushAsync(latestState).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Saves the latest pending payload immediately and optionally folds in one final captured payload before writing.
    /// </summary>
    public async Task FlushAsync(TState? latestState = null)
    {
        if (latestState != null)
        {
            QueueState(latestState, scheduleDelay: false);
        }
        else
        {
            CancelPendingDelay();
        }

        await SavePendingAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels any outstanding debounce timer so the saver stops accepting new delayed work.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        CancelPendingDelay();
    }

    /// <summary>
    /// Records one payload as the latest pending save state and optionally restarts the debounce timer.
    /// </summary>
    private void QueueState(TState state, bool scheduleDelay)
    {
        CancellationTokenSource? previousDelaySource;
        CancellationTokenSource? nextDelaySource = null;

        lock (_gate)
        {
            ThrowIfDisposed();
            _pendingState = _hasPendingState ? _mergeStates(_pendingState!, state) : state;
            _hasPendingState = true;
            previousDelaySource = DetachDelayCancellationSource();

            if (scheduleDelay)
            {
                nextDelaySource = new CancellationTokenSource();
                _delayCancellationSource = nextDelaySource;
            }
        }

        previousDelaySource?.Cancel();
        previousDelaySource?.Dispose();

        if (nextDelaySource != null)
        {
            _ = RunDelayedSaveAsync(nextDelaySource.Token);
        }
    }

    /// <summary>
    /// Waits out the debounce window before saving the latest pending payload.
    /// </summary>
    private async Task RunDelayedSaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_debounceDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SavePendingAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Serializes saves so only one background write runs at a time while still picking up any queued follow-up edits.
    /// </summary>
    private async Task SavePendingAsync()
    {
        await _saveSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            while (true)
            {
                TState? stateToSave;
                lock (_gate)
                {
                    if (!_hasPendingState)
                    {
                        return;
                    }

                    stateToSave = _pendingState;
                    _pendingState = null;
                    _hasPendingState = false;
                }

                try
                {
                    // Run the save delegate on the thread pool so UI callers can flush without performing disk I/O on
                    // the dispatcher thread.
                    await Task.Run(() => _saveState(stateToSave!)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _handleSaveException?.Invoke(ex);
                }
            }
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }

    /// <summary>
    /// Cancels the active debounce timer, if one exists, so the next flush can save immediately.
    /// </summary>
    private void CancelPendingDelay()
    {
        CancellationTokenSource? delaySource;
        lock (_gate)
        {
            delaySource = DetachDelayCancellationSource();
        }

        delaySource?.Cancel();
        delaySource?.Dispose();
    }

    /// <summary>
    /// Detaches the current debounce token source from the saver so callers can cancel it outside the lock.
    /// </summary>
    private CancellationTokenSource? DetachDelayCancellationSource()
    {
        CancellationTokenSource? delaySource = _delayCancellationSource;
        _delayCancellationSource = null;
        return delaySource;
    }

    /// <summary>
    /// Throws when callers try to queue additional work after disposal.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DebouncedBackgroundSaver<TState>));
        }
    }

    /// <summary>
    /// Replaces any older queued payload with the most recent payload when callers do not need custom merge logic.
    /// </summary>
    private static TState ReplacePendingState(TState _, TState newer)
    {
        return newer;
    }
}
