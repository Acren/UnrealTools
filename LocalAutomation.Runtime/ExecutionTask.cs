using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Distinguishes structural scope tasks from internal body tasks that carry the runnable work for authored `.Run(...)`
/// declarations.
/// </summary>
public enum ExecutionTaskKind
{
    Scope,
    Body
}

/// <summary>
/// Represents one execution-graph node. Authored plans and live sessions share this type, but runtime sessions own their
/// own cloned task instances so mutable graph state, runtime state, and task-scoped logs never leak back into preview
/// plan objects.
/// </summary>
public sealed class ExecutionTask : INotifyPropertyChanged
{
    private ExecutionTaskId? _parentId;
    private ExecutionTaskState _state;
    private string _statusReason;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;
    private ExecutionTaskOutcome? _outcome;
    private readonly Dictionary<Type, object> _stateByType = new();
    private readonly object _stateWaitSyncRoot = new();
    private readonly HashSet<ExecutionTaskState> _reachedStates = new();
    private readonly Dictionary<ExecutionTaskState, List<TaskCompletionSource<bool>>> _stateWaiters = new();

    /// <summary>
    /// Creates one execution task from authored metadata. Sessions later clone these tasks into live nodes and initialize
    /// their mutable runtime state independently.
    /// </summary>
    public ExecutionTask(
        ExecutionTaskId id,
        string title,
        Operation operation,
        string? description = null,
        ExecutionTaskId? parentId = null,
        IEnumerable<ExecutionTaskId>? dependsOn = null,
        bool enabled = true,
        string? disabledReason = null,
        OperationParameters? operationParameters = null,
        IEnumerable<Type>? declaredOptionTypes = null,
        Func<ExecutionTaskContext, Task<OperationResult>>? executeAsync = null,
        ExecutionTaskOutcome? outcome = null,
        bool isOperationRoot = false,
        bool isHiddenInGraph = false,
        ExecutionTaskKind kind = ExecutionTaskKind.Scope,
        ExecutionTaskId? bodyOwnerTaskId = null)
    {
        Id = id;
        Title = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("Execution task title is required.", nameof(title))
            : title;
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        Description = description ?? string.Empty;
        _parentId = parentId;
        _dependsOn = new List<ExecutionTaskId>((dependsOn ?? Array.Empty<ExecutionTaskId>()));
        DependsOn = new ReadOnlyCollection<ExecutionTaskId>(_dependsOn);
        _childTaskIds = new List<ExecutionTaskId>();
        ChildTaskIds = new ReadOnlyCollection<ExecutionTaskId>(_childTaskIds);
        Enabled = enabled;
        DisabledReason = enabled ? string.Empty : (disabledReason ?? string.Empty);
        OperationParameters = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters));
        DeclaredOptionTypes = (declaredOptionTypes ?? Array.Empty<Type>()).ToList().AsReadOnly();
        ExecuteAsync = executeAsync;
        Outcome = outcome;
        IsOperationRoot = isOperationRoot;
        IsHiddenInGraph = isHiddenInGraph;
        Kind = kind;
        BodyOwnerTaskId = bodyOwnerTaskId;
        LogStream = new BufferedLogStream();

        /* Runtime lifecycle and semantic outcome are tracked separately. Disabled tasks start in a completed lifecycle
           state because no scheduler work remains for them, while their semantic outcome still reports Disabled. */
        _state = enabled ? ExecutionTaskState.Planned : ExecutionTaskState.Completed;
        _statusReason = enabled ? string.Empty : DisabledReason;
        _outcome = enabled ? outcome : ExecutionTaskOutcome.Disabled;
        ResetReachedStates(_state);
    }

    private readonly List<ExecutionTaskId> _dependsOn;
    private readonly List<ExecutionTaskId> _childTaskIds;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ExecutionTaskId Id { get; }

    public string Title { get; }

    public Operation Operation { get; }

    public string Description { get; }

    public ExecutionTaskId? ParentId
    {
        get => _parentId;
        internal set => SetProperty(ref _parentId, value);
    }

    public IReadOnlyList<ExecutionTaskId> DependsOn { get; }

    public IReadOnlyList<ExecutionTaskId> ChildTaskIds { get; }

    public bool Enabled { get; }

    public string DisabledReason { get; }

    public OperationParameters OperationParameters { get; }

    public IReadOnlyList<Type> DeclaredOptionTypes { get; }

    public Func<ExecutionTaskContext, Task<OperationResult>>? ExecuteAsync { get; }

    /// <summary>
    /// Gets the current execution lifecycle status for this task scope.
    /// </summary>
    public ExecutionTaskState State
    {
        get => _state;
        internal set => SetProperty(ref _state, value);
    }

    public string StatusReason
    {
        get => _statusReason;
        internal set => SetProperty(ref _statusReason, value ?? string.Empty);
    }

    public DateTimeOffset? StartedAt
    {
        get => _startedAt;
        internal set => SetProperty(ref _startedAt, value);
    }

    public DateTimeOffset? FinishedAt
    {
        get => _finishedAt;
        internal set => SetProperty(ref _finishedAt, value);
    }

    /// <summary>
    /// Gets the semantic outcome for this task scope once known. A task may still be running while this result is already
    /// determined, such as when a child failure has doomed the scope but cleanup is still unwinding.
    /// </summary>
    public ExecutionTaskOutcome? Outcome
    {
        get => _outcome;
        internal set => SetProperty(ref _outcome, value);
    }

    /// <summary>
    /// Gets whether this task is the root container for one operation subtree. Operation-scoped context walks upward to
    /// the nearest task with this marker so nested child operations keep independent state scopes.
    /// </summary>
    public bool IsOperationRoot { get; }

    /// <summary>
    /// Gets whether this task should be collapsed out of the graph projection unless the UI is configured to reveal
    /// hidden tasks.
    /// </summary>
    public bool IsHiddenInGraph { get; }

    /// <summary>
    /// Gets whether this task is a structural scope node or one internal body node lowered from an authored `.Run(...)`
    /// declaration.
    /// </summary>
    public ExecutionTaskKind Kind { get; }

    /// <summary>
    /// Gets the structural scope task that owns this body task when one exists.
    /// </summary>
    public ExecutionTaskId? BodyOwnerTaskId { get; }

    public BufferedLogStream LogStream { get; }

    /// <summary>
    /// Waits until this task reaches the requested runtime state at least once during the current session lifetime.
    /// </summary>
    public Task WaitForStateAsync(ExecutionTaskState targetState, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        TaskCompletionSource<bool>? completionSource;
        CancellationTokenRegistration cancellationRegistration = default;
        lock (_stateWaitSyncRoot)
        {
            if (_reachedStates.Contains(targetState))
            {
                return Task.CompletedTask;
            }

            if (_state == ExecutionTaskState.Completed)
            {
                return Task.FromException(CreateUnreachableStateException(targetState));
            }

            completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_stateWaiters.TryGetValue(targetState, out List<TaskCompletionSource<bool>>? waiters))
            {
                waiters = new List<TaskCompletionSource<bool>>();
                _stateWaiters[targetState] = waiters;
            }

            waiters.Add(completionSource);
            if (cancellationToken.CanBeCanceled)
            {
                cancellationRegistration = cancellationToken.Register(() =>
                {
                    lock (_stateWaitSyncRoot)
                    {
                        if (_stateWaiters.TryGetValue(targetState, out List<TaskCompletionSource<bool>>? registrations))
                        {
                            registrations.Remove(completionSource);
                            if (registrations.Count == 0)
                            {
                                _stateWaiters.Remove(targetState);
                            }
                        }
                    }

                    completionSource.TrySetCanceled(cancellationToken);
                });
            }
        }

        return AwaitStateAsync(completionSource, cancellationRegistration);
    }

    /// <summary>
    /// Waits until this task starts executing.
    /// </summary>
    public Task WaitForStartAsync(CancellationToken cancellationToken = default)
    {
        return WaitForStateAsync(ExecutionTaskState.Running, cancellationToken);
    }

    /// <summary>
    /// Waits until this task reaches a completed runtime state.
    /// </summary>
    public Task WaitForCompletionAsync(CancellationToken cancellationToken = default)
    {
        return WaitForStateAsync(ExecutionTaskState.Completed, cancellationToken);
    }

    internal ExecutionTask CloneForSession()
    {
        /* Session-owned clones preserve graph visibility metadata so preview and runtime tabs collapse the same authored
           internal tasks unless the user explicitly reveals hidden nodes in the UI. */
        return new ExecutionTask(Id, Title, Operation, Description, ParentId, _dependsOn, Enabled, DisabledReason, OperationParameters, DeclaredOptionTypes, ExecuteAsync, Outcome, IsOperationRoot, IsHiddenInGraph, Kind, BodyOwnerTaskId);
    }

    internal void InitializeRuntimeState(bool hasBegunExecution)
    {
        /* Sessions reset only lifecycle state here. Semantic outcome stays separate so disabled tasks can still render as
           Disabled while the scheduler treats them as already finished. */
        ExecutionTaskState initialState = Enabled
            ? (hasBegunExecution ? ExecutionTaskState.Pending : ExecutionTaskState.Planned)
            : ExecutionTaskState.Completed;
        State = initialState;
        StatusReason = Enabled ? string.Empty : DisabledReason;
        Outcome = Enabled ? null : ExecutionTaskOutcome.Disabled;
        StartedAt = null;
        FinishedAt = null;
        ResetReachedStates(initialState);
    }

    internal void AddChild(ExecutionTaskId childTaskId)
    {
        if (_childTaskIds.Contains(childTaskId))
        {
            return;
        }

        _childTaskIds.Add(childTaskId);
        RaisePropertyChanged(nameof(ChildTaskIds));
    }

    internal void AddDependency(ExecutionTaskId dependencyTaskId)
    {
        if (_dependsOn.Contains(dependencyTaskId))
        {
            return;
        }

        _dependsOn.Add(dependencyTaskId);
        RaisePropertyChanged(nameof(DependsOn));
    }

    internal void SetState<T>(T value) where T : class
    {
        _stateByType[typeof(T)] = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal bool TryGetLocalState<T>(out T? value) where T : class
    {
        if (_stateByType.TryGetValue(typeof(T), out object? rawValue) && rawValue is T typedValue)
        {
            value = typedValue;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Applies one complete runtime-state snapshot and raises property notifications only after the full state is
    /// internally consistent. This prevents observers from seeing torn combinations such as Running plus Result=Completed.
    /// </summary>
    internal void ApplyRuntimeState(ExecutionTaskState state, string statusReason, ExecutionTaskOutcome? outcome, DateTimeOffset? startedAt, DateTimeOffset? finishedAt)
    {
        bool stateChanged = !Equals(_state, state);
        bool statusReasonChanged = !Equals(_statusReason, statusReason);
        bool outcomeChanged = !Equals(_outcome, outcome);
        bool startedAtChanged = !Equals(_startedAt, startedAt);
        bool finishedAtChanged = !Equals(_finishedAt, finishedAt);

        _state = state;
        _statusReason = statusReason;
        _outcome = outcome;
        _startedAt = startedAt;
        _finishedAt = finishedAt;

        List<TaskCompletionSource<bool>> completedWaiters = new();
        List<TaskCompletionSource<bool>> failedWaiters = new();
        lock (_stateWaitSyncRoot)
        {
            bool reachedNewState = _reachedStates.Add(state);
            if (reachedNewState && _stateWaiters.TryGetValue(state, out List<TaskCompletionSource<bool>>? matchingWaiters))
            {
                completedWaiters.AddRange(matchingWaiters);
                _stateWaiters.Remove(state);
            }

            if (state == ExecutionTaskState.Completed)
            {
                foreach ((ExecutionTaskState waiterState, List<TaskCompletionSource<bool>> waiters) in _stateWaiters.ToList())
                {
                    if (_reachedStates.Contains(waiterState))
                    {
                        completedWaiters.AddRange(waiters);
                    }
                    else
                    {
                        failedWaiters.AddRange(waiters);
                    }

                    _stateWaiters.Remove(waiterState);
                }
            }
        }

        foreach (TaskCompletionSource<bool> waiter in completedWaiters)
        {
            waiter.TrySetResult(true);
        }

        if (state == ExecutionTaskState.Completed)
        {
            InvalidOperationException unreachableStateException = CreateUnreachableStateException(null);
            foreach (TaskCompletionSource<bool> waiter in failedWaiters)
            {
                waiter.TrySetException(unreachableStateException);
            }
        }

        if (stateChanged)
        {
            RaisePropertyChanged(nameof(State));
        }

        if (statusReasonChanged)
        {
            RaisePropertyChanged(nameof(StatusReason));
        }

        if (outcomeChanged)
        {
            RaisePropertyChanged(nameof(Outcome));
        }

        if (startedAtChanged)
        {
            RaisePropertyChanged(nameof(StartedAt));
        }

        if (finishedAtChanged)
        {
            RaisePropertyChanged(nameof(FinishedAt));
        }
    }

    private void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        RaisePropertyChanged(propertyName);
    }

    /// <summary>
    /// Resets the reached-state set when a live session reinitializes this task for a new run.
    /// </summary>
    private void ResetReachedStates(ExecutionTaskState initialState)
    {
        lock (_stateWaitSyncRoot)
        {
            _reachedStates.Clear();
            _reachedStates.Add(initialState);
            _stateWaiters.Clear();
        }
    }

    /// <summary>
    /// Awaits one registered state waiter and always disposes its cancellation registration.
    /// </summary>
    private static async Task AwaitStateAsync(TaskCompletionSource<bool> completionSource, CancellationTokenRegistration cancellationRegistration)
    {
        try
        {
            await completionSource.Task.ConfigureAwait(false);
        }
        finally
        {
            cancellationRegistration.Dispose();
        }
    }

    /// <summary>
    /// Creates a consistent exception when a caller waits for a state the task can no longer reach.
    /// </summary>
    private InvalidOperationException CreateUnreachableStateException(ExecutionTaskState? targetState)
    {
        return targetState == null
            ? new InvalidOperationException($"Task '{Title}' ({Id}) completed before one or more awaited states were reached.")
            : new InvalidOperationException($"Task '{Title}' ({Id}) completed before reaching awaited state '{targetState}'.");
    }
}
