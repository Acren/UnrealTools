using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Runtime;

internal enum TaskStartState
{
    Ready,
    WaitingForDependencies,
    WaitingForParent,
    Running,
    NoStartableWork
}

internal sealed class TaskStartResult
{
    public TaskStartResult(ExecutionTask task, Task<OperationResult> runningTask)
    {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        RunningTask = runningTask ?? throw new ArgumentNullException(nameof(runningTask));
    }

    public ExecutionTask Task { get; }

    public Task<OperationResult> RunningTask { get; }
}

/// <summary>
/// Represents one execution-graph node. Authored plans and live sessions share this type, but runtime sessions own their
/// own cloned task instances so mutable graph state, runtime state, and task-scoped logs never leak back into preview
/// plan objects.
/// </summary>
public class ExecutionTask : INotifyPropertyChanged
{
    private ExecutionTaskId? _parentId;
    private string _description;
    private ExecutionTaskState _state;
    private string _statusReason;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;
    private ExecutionTaskOutcome? _outcome;
    private bool _enabled;
    private string _disabledReason;
    private OperationParameters _operationParameters;
    private IReadOnlyList<Type> _declaredOptionTypes;
    private readonly Func<ExecutionTaskContext, Task<OperationResult>>? _executeAsync;
    private bool _isOperationRoot;
    private bool _isHiddenInGraph;
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
        bool isHiddenInGraph = false)
    {
        Id = id;
        Title = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("Execution task title is required.", nameof(title))
            : title;
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        _description = description ?? string.Empty;
        _parentId = parentId;
        _dependsOn = new List<ExecutionTaskId>((dependsOn ?? Array.Empty<ExecutionTaskId>()));
        DependsOn = new ReadOnlyCollection<ExecutionTaskId>(_dependsOn);
        _enabled = enabled;
        _disabledReason = enabled ? string.Empty : (disabledReason ?? string.Empty);
        _operationParameters = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters));
        _declaredOptionTypes = (declaredOptionTypes ?? Array.Empty<Type>()).ToList().AsReadOnly();
        _executeAsync = executeAsync;
        _childTaskIds = new List<ExecutionTaskId>();
        ChildTaskIds = new ReadOnlyCollection<ExecutionTaskId>(_childTaskIds);
        Outcome = outcome;
        _isOperationRoot = isOperationRoot;
        _isHiddenInGraph = isHiddenInGraph;
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

    public string Description => _description;

    public ExecutionTaskId? ParentId
    {
        get => _parentId;
        internal set => SetProperty(ref _parentId, value);
    }

    public IReadOnlyList<ExecutionTaskId> DependsOn { get; }

    public IReadOnlyList<ExecutionTaskId> ChildTaskIds { get; }

    public bool Enabled => _enabled;

    public string DisabledReason => _disabledReason;

    public OperationParameters OperationParameters => _operationParameters;

    public IReadOnlyList<Type> DeclaredOptionTypes => _declaredOptionTypes;

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
    public bool IsOperationRoot => _isOperationRoot;

    /// <summary>
    /// Gets whether this task should be collapsed out of the graph projection unless the UI is configured to reveal
    /// hidden tasks.
    /// </summary>
    public bool IsHiddenInGraph => _isHiddenInGraph;

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
        return CreateClone(ParentId, Title, Description, IsHiddenInGraph, Outcome);
    }

    /// <summary>
    /// Creates one task clone while preserving any internal runnable work and authored graph metadata.
    /// </summary>
    internal ExecutionTask CreateClone(ExecutionTaskId? parentId, string title, string description, bool isHiddenInGraph, ExecutionTaskOutcome? outcome = null)
    {
        return new ExecutionTask(
            Id,
            title,
            Operation,
            description,
            parentId,
            DependsOn,
            Enabled,
            DisabledReason,
            OperationParameters,
            DeclaredOptionTypes,
            _executeAsync,
            outcome,
            IsOperationRoot,
            isHiddenInGraph);
    }

    /// <summary>
    /// Returns the child task ids owned by this task. Tasks that do not aggregate children return an empty list.
    /// </summary>
    internal IReadOnlyList<ExecutionTaskId> GetChildTaskIds()
    {
        return ChildTaskIds;
    }

    /// <summary>
    /// Returns the current scheduler-facing start state for this task subtree.
    /// </summary>
    internal TaskStartState GetTaskStartState(ExecutionSession session)
    {
        if (session == null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        if (State == ExecutionTaskState.Completed)
        {
            return TaskStartState.NoStartableWork;
        }

        if (CanStartOwnWork(session))
        {
            return TaskStartState.Ready;
        }

        if (GetChildTaskIds().Any(childTaskId => session.GetTask(childTaskId).GetTaskStartState(session) == TaskStartState.Ready))
        {
            return TaskStartState.Ready;
        }

        if ((_executeAsync != null && State == ExecutionTaskState.Running) || GetChildTaskIds().Any(childTaskId => session.GetTask(childTaskId).GetTaskStartState(session) == TaskStartState.Running))
        {
            return TaskStartState.Running;
        }

        if (_executeAsync != null && State == ExecutionTaskState.Pending)
        {
            if (!session.AreTaskDependenciesSatisfied(this))
            {
                return TaskStartState.WaitingForDependencies;
            }

            if (!session.AreTaskAncestorsOpen(this))
            {
                return TaskStartState.WaitingForParent;
            }
        }

        if (GetChildTaskIds().Any(childTaskId => session.GetTask(childTaskId).GetTaskStartState(session) == TaskStartState.WaitingForDependencies))
        {
            return TaskStartState.WaitingForDependencies;
        }

        if (GetChildTaskIds().Any(childTaskId => session.GetTask(childTaskId).GetTaskStartState(session) == TaskStartState.WaitingForParent))
        {
            return TaskStartState.WaitingForParent;
        }

        return TaskStartState.NoStartableWork;
    }

    /// <summary>
    /// Returns the execution locks required for the next startable work item in this task subtree.
    /// </summary>
    internal IReadOnlyList<ExecutionLock> GetExecutionLocksForNextStart(ExecutionSession session)
    {
        if (session == null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        foreach (ExecutionTaskId childTaskId in GetChildTaskIds())
        {
            ExecutionTask childTask = session.GetTask(childTaskId);
            if (childTask.GetTaskStartState(session) == TaskStartState.Ready)
            {
                return childTask.GetExecutionLocksForNextStart(session);
            }
        }

        if (CanStartOwnWork(session))
        {
            return Operation.GetDeclaredExecutionLocks(new ValidatedOperationParameters(Title, OperationParameters, DeclaredOptionTypes));
        }

        return Array.Empty<ExecutionLock>();
    }

    /// <summary>
    /// Starts the next startable work item in this task subtree and returns the actual task that entered Running.
    /// </summary>
    internal TaskStartResult StartAsync(ExecutionTaskContext context)
    {
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        ExecutionTaskRuntimeServices runtime = context.GetRequiredRuntime();

        foreach (ExecutionTaskId childTaskId in GetChildTaskIds())
        {
            ExecutionTask childTask = runtime.GetTask(childTaskId);
            if (runtime.AreTaskReadyToStart(childTask))
            {
                return childTask.StartAsync(context.CreateForTask(childTask));
            }
        }

        if (!runtime.CanTaskStartOwnWork(this))
        {
            throw new InvalidOperationException($"Task '{Id}' does not have any work ready to start.");
        }

        runtime.SetTaskState(Id, ExecutionTaskState.Running);
        return new TaskStartResult(this, _executeAsync!(context));
    }

    /// <summary>
    /// Adds one child task to this task. Tasks that do not aggregate children fail loudly instead of silently accepting an
    /// invalid graph shape.
    /// </summary>
    internal void AddChild(ExecutionTaskId childTaskId)
    {
        if (_childTaskIds.Contains(childTaskId))
        {
            return;
        }

        _childTaskIds.Add(childTaskId);
        RaisePropertyChanged(nameof(ChildTaskIds));
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

    internal void AddDependency(ExecutionTaskId dependencyTaskId)
    {
        if (_dependsOn.Contains(dependencyTaskId))
        {
            return;
        }

        _dependsOn.Add(dependencyTaskId);
        RaisePropertyChanged(nameof(DependsOn));
    }

    /// <summary>
    /// Updates builder-owned presentation metadata before the task graph becomes a live runtime session.
    /// </summary>
    internal void SetDescription(string description)
    {
        _description = description ?? string.Empty;
    }

    /// <summary>
    /// Updates whether the task participates in the authored plan and the user-facing reason when it does not.
    /// </summary>
    internal void SetCondition(bool enabled, string? disabledReason)
    {
        _enabled = enabled;
        _disabledReason = enabled ? string.Empty : (disabledReason ?? string.Empty);
    }

    /// <summary>
    /// Assigns the validated parameter context that builder-authored execution will use at runtime.
    /// </summary>
    internal void SetOperationParameters(OperationParameters operationParameters, IEnumerable<Type> declaredOptionTypes)
    {
        _operationParameters = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters));
        _declaredOptionTypes = (declaredOptionTypes ?? throw new ArgumentNullException(nameof(declaredOptionTypes))).ToList().AsReadOnly();
    }

    /// <summary>
    /// Marks whether this authored task should be treated as an operation root for scoped runtime state.
    /// </summary>
    internal void SetOperationRoot(bool isOperationRoot)
    {
        _isOperationRoot = isOperationRoot;
    }

    /// <summary>
    /// Controls whether this authored task is hidden in the graph projection until debugging reveals internal nodes.
    /// </summary>
    internal void SetHiddenInGraph(bool isHiddenInGraph)
    {
        _isHiddenInGraph = isHiddenInGraph;
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

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
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

    /// <summary>
    /// Returns whether this task's directly attached runnable work can start immediately.
    /// </summary>
    internal bool CanStartOwnWork(ExecutionSession session)
    {
        return _executeAsync != null
            && State == ExecutionTaskState.Pending
            && session.AreTaskDependenciesSatisfied(this)
            && session.AreTaskAncestorsOpen(this);
    }

    /// <summary>
    /// Returns whether this task has entered real execution instead of remaining untouched queued work.
    /// </summary>
    internal bool HasStarted =>
        State == ExecutionTaskState.Running
        || StartedAt != null
        || Outcome is ExecutionTaskOutcome.Completed or ExecutionTaskOutcome.Cancelled or ExecutionTaskOutcome.Interrupted or ExecutionTaskOutcome.Failed;

    /// <summary>
    /// Resolves the timing timestamps that correspond to a lifecycle state transition on this task. Running tasks capture
    /// their start time, and completing tasks capture both start and finish times.
    /// </summary>
    internal (DateTimeOffset? startedAt, DateTimeOffset? finishedAt) ResolveTaskTiming(ExecutionTaskState state)
    {
        DateTimeOffset? startedAt = StartedAt;
        DateTimeOffset? finishedAt = FinishedAt;
        DateTimeOffset timestamp = DateTimeOffset.Now;

        if (state == ExecutionTaskState.Running && StartedAt == null)
        {
            startedAt = timestamp;
            finishedAt = null;
            return (startedAt, finishedAt);
        }

        if (state == ExecutionTaskState.Completed)
        {
            startedAt ??= timestamp;
            finishedAt = timestamp;
        }

        return (startedAt, finishedAt);
    }

    /// <summary>
    /// Guards lifecycle transitions so tasks with direct execution move monotonically while parent tasks may return from
    /// Running to Pending when all active descendants stop and later descendants are still queued.
    /// </summary>
    internal void ValidateStateTransition(ExecutionSession session, ExecutionTaskState nextState)
    {
        if (State == ExecutionTaskState.Completed && nextState != ExecutionTaskState.Completed)
        {
            throw new InvalidOperationException($"Task '{Id}' cannot transition from completed execution state back to '{nextState}'.");
        }

        if (State == ExecutionTaskState.Running && nextState is ExecutionTaskState.Pending or ExecutionTaskState.Planned)
        {
            /* Parent tasks may return from Running to Pending when all active descendants finish and later descendants
               are still queued. This is only valid for container tasks whose subtree is not actively running. */
            if (GetChildTaskIds().Count > 0 && GetTaskStartState(session) != TaskStartState.Running && nextState == ExecutionTaskState.Pending)
            {
                return;
            }

            throw new InvalidOperationException($"Task '{Id}' cannot transition from running to '{nextState}'.");
        }
    }

    /// <summary>
    /// Guards combined observable state so lifecycle and semantic outcome never describe contradictory execution states.
    /// </summary>
    internal void ValidateObservedState(ExecutionTaskState state, ExecutionTaskOutcome? outcome)
    {
        if (state == ExecutionTaskState.Running && outcome is ExecutionTaskOutcome.Completed or ExecutionTaskOutcome.Skipped or ExecutionTaskOutcome.Disabled)
        {
            throw new InvalidOperationException($"Task '{Id}' cannot be running while reporting semantic outcome '{outcome}'.");
        }

        if (state is ExecutionTaskState.Planned or ExecutionTaskState.Pending && outcome != null)
        {
            throw new InvalidOperationException($"Task '{Id}' cannot remain queued while reporting semantic outcome '{outcome}'.");
        }

        if (state == ExecutionTaskState.Completed && outcome == null)
        {
            throw new InvalidOperationException($"Task '{Id}' cannot complete its execution state without a semantic outcome.");
        }
    }

    /// <summary>
    /// Guards semantic result transitions so only stricter outcomes can replace a previously assigned result.
    /// </summary>
    internal void ValidateOutcomeTransition(ExecutionTaskOutcome? nextOutcome)
    {
        if (Outcome == null || nextOutcome == null || Outcome == nextOutcome)
        {
            return;
        }

        if (CanUpgradeTaskOutcome(Outcome.Value, nextOutcome.Value))
        {
            return;
        }

        throw new InvalidOperationException($"Task '{Id}' cannot change outcome from '{Outcome}' to '{nextOutcome}'.");
    }

    /// <summary>
    /// Defines the small set of semantic result upgrades that remain legal after a task has already been marked with a
    /// weaker doomed outcome.
    /// </summary>
    internal static bool CanUpgradeTaskOutcome(ExecutionTaskOutcome currentOutcome, ExecutionTaskOutcome nextOutcome)
    {
        return (currentOutcome, nextOutcome) switch
        {
            (ExecutionTaskOutcome.Interrupted, ExecutionTaskOutcome.Failed) => true,
            (ExecutionTaskOutcome.Cancelled, ExecutionTaskOutcome.Failed) => true,
            _ => false
        };
    }

    /// <summary>
    /// Guards terminal assignments so untouched-only outcomes cannot be applied after a task or subtree has started real
    /// execution.
    /// </summary>
    internal void ValidateTerminalAssignment(ExecutionSession session, ExecutionTaskOutcome outcome)
    {
        if (outcome == ExecutionTaskOutcome.Completed && HasNonTerminalDescendants(session))
        {
            throw new InvalidOperationException($"Task '{Title}' ({Id}) cannot complete successfully while it still has non-terminal descendants.");
        }

        if (outcome == ExecutionTaskOutcome.Skipped && HasStartedWorkInSubtree(session))
        {
            throw new InvalidOperationException($"Task '{Title}' ({Id}) cannot be marked skipped after execution has started in its subtree.");
        }

        if (outcome == ExecutionTaskOutcome.Interrupted && !HasStartedWorkInSubtree(session))
        {
            throw new InvalidOperationException($"Task '{Title}' ({Id}) cannot be marked interrupted before execution has started in its subtree.");
        }
    }

    /// <summary>
    /// Returns whether any task in this subtree has already transitioned out of its untouched planned or pending state.
    /// </summary>
    internal bool HasStartedWorkInSubtree(ExecutionSession session)
    {
        if (HasStarted)
        {
            return true;
        }

        return GetChildTaskIds().Any(childTaskId => session.GetTask(childTaskId).HasStartedWorkInSubtree(session));
    }

    /// <summary>
    /// Returns whether any descendant beneath this task is still pending or running.
    /// </summary>
    internal bool HasNonTerminalDescendants(ExecutionSession session)
    {
        return GetChildTaskIds().Any(childTaskId =>
        {
            ExecutionTask childTask = session.GetTask(childTaskId);
            return childTask.State != ExecutionTaskState.Completed || childTask.HasNonTerminalDescendants(session);
        });
    }

    /// <summary>
    /// Builds the human-readable status reason that untouched descendants inherit when their parent completes with a
    /// non-success outcome.
    /// </summary>
    internal string? BuildInheritedDescendantReason(ExecutionTaskOutcome outcome, string? statusReason)
    {
        if (!string.IsNullOrWhiteSpace(statusReason))
        {
            return outcome == ExecutionTaskOutcome.Skipped
                ? statusReason
                : $"Skipped because parent task '{Id}' {outcome.ToString().ToLowerInvariant()}: {statusReason}";
        }

        return outcome switch
        {
            ExecutionTaskOutcome.Skipped => $"Skipped because parent task '{Id}' was skipped.",
            ExecutionTaskOutcome.Cancelled => $"Skipped because parent task '{Id}' was cancelled.",
            ExecutionTaskOutcome.Interrupted => $"Skipped because parent task '{Id}' was interrupted.",
            ExecutionTaskOutcome.Failed => $"Skipped because parent task '{Id}' failed.",
            _ => null
        };
    }

    /// <summary>
    /// Skips every unfinished task in this subtree without disturbing tasks that already reached terminal states. Each
    /// skipped task validates its terminal assignment, transitions to completed, propagates to its own descendants, and
    /// notifies the session so events fire from the authoritative event surface.
    /// </summary>
    internal void SkipUnfinishedSubtree(ExecutionSession session, string? reason, ExecutionTaskOutcome skippedOutcome = ExecutionTaskOutcome.Skipped)
    {
        if (State is ExecutionTaskState.Planned or ExecutionTaskState.Pending)
        {
            CompleteWithOutcome(session, skippedOutcome, reason);
        }

        foreach (ExecutionTaskId childTaskId in GetChildTaskIds())
        {
            session.GetTask(childTaskId).SkipUnfinishedSubtree(session, reason, skippedOutcome);
        }
    }

    /// <summary>
    /// Skips every unfinished descendant beneath this task by walking each direct child subtree.
    /// </summary>
    internal void SkipUnfinishedDescendants(ExecutionSession session, string? reason, ExecutionTaskOutcome skippedOutcome = ExecutionTaskOutcome.Skipped)
    {
        foreach (ExecutionTaskId childTaskId in GetChildTaskIds())
        {
            session.GetTask(childTaskId).SkipUnfinishedSubtree(session, reason, skippedOutcome);
        }
    }

    /// <summary>
    /// Marks one started subtree as interrupted after sibling failure. Started nodes keep their current lifecycle so any
    /// active descendant work can finish unwinding, while untouched nodes become skipped because they never ran.
    /// </summary>
    internal void MarkSubtreeInterrupted(ExecutionSession session, string? reason)
    {
        if (State != ExecutionTaskState.Completed)
        {
            if (HasStarted)
            {
                /* Started tasks receive an interrupted outcome without forcing lifecycle completion so any active
                   descendant work can finish unwinding naturally. */
                TransitionOutcome(ExecutionTaskOutcome.Interrupted, reason);
                session.NotifyTaskChanged(Id);
            }
            else
            {
                CompleteWithOutcome(session, ExecutionTaskOutcome.Skipped, reason);
            }
        }

        foreach (ExecutionTaskId childTaskId in GetChildTaskIds())
        {
            session.GetTask(childTaskId).MarkSubtreeInterrupted(session, reason);
        }
    }

    /// <summary>
    /// Interrupts one sibling subtree after a different sibling has failed. Subtrees that already started work surface as
    /// Interrupted, while untouched subtrees remain Skipped.
    /// </summary>
    internal void InterruptSiblingSubtree(ExecutionSession session, ExecutionTaskId failedSiblingRootId)
    {
        bool subtreeStarted = HasStartedWorkInSubtree(session);
        string reason = subtreeStarted
            ? $"Interrupted because sibling task '{failedSiblingRootId}' failed."
            : $"Skipped because sibling task '{failedSiblingRootId}' failed.";

        if (subtreeStarted)
        {
            MarkSubtreeInterrupted(session, reason);
            return;
        }

        SkipUnfinishedSubtree(session, reason, ExecutionTaskOutcome.Skipped);
    }

    /// <summary>
    /// Propagates a terminal outcome to untouched descendants so tasks that never started inherit a completed lifecycle
    /// plus the semantic outcome that explains why they will never run.
    /// </summary>
    internal void PropagateTerminalOutcomeToUntouchedDescendants(ExecutionSession session, ExecutionTaskOutcome outcome, string? statusReason)
    {
        /* Descendants that never started inherit a completed lifecycle plus the semantic outcome that explains why they
           will never run. */
        ExecutionTaskOutcome descendantOutcome = outcome switch
        {
            ExecutionTaskOutcome.Skipped => ExecutionTaskOutcome.Skipped,
            ExecutionTaskOutcome.Cancelled => ExecutionTaskOutcome.Skipped,
            ExecutionTaskOutcome.Interrupted => ExecutionTaskOutcome.Skipped,
            ExecutionTaskOutcome.Failed => ExecutionTaskOutcome.Skipped,
            _ => default
        };

        if (descendantOutcome == default)
        {
            return;
        }

        string? descendantReason = BuildInheritedDescendantReason(outcome, statusReason);
        SkipUnfinishedDescendants(session, descendantReason, descendantOutcome);
    }

    /// <summary>
    /// Completes this task's lifecycle and assigns its semantic outcome, then propagates to untouched descendants. The
    /// session fires the externally visible state-changed event after each mutation. Ancestor refresh is handled by the
    /// session-level caller since it requires cross-branch coordination.
    /// </summary>
    internal void CompleteWithOutcome(ExecutionSession session, ExecutionTaskOutcome outcome, string? statusReason = null)
    {
        ValidateTerminalAssignment(session, outcome);
        TransitionSnapshot(session, ExecutionTaskState.Completed, outcome, statusReason);
        session.NotifyTaskChanged(Id);
        PropagateTerminalOutcomeToUntouchedDescendants(session, outcome, statusReason);
    }

    /// <summary>
    /// Completes this task's lifecycle while preserving any previously assigned doomed outcome (failed, cancelled, or
    /// interrupted) that was recorded while descendant work was still unwinding. When no doomed outcome exists, the
    /// provided success outcome is used instead.
    /// </summary>
    internal void CompleteLifecycle(ExecutionSession session, ExecutionTaskOutcome successOutcome = ExecutionTaskOutcome.Completed, string? statusReason = null)
    {
        ExecutionTaskOutcome finalOutcome = Outcome is ExecutionTaskOutcome.Failed or ExecutionTaskOutcome.Cancelled or ExecutionTaskOutcome.Interrupted
            ? Outcome.Value
            : successOutcome;
        string? finalReason = finalOutcome == successOutcome ? statusReason : StatusReason;
        CompleteWithOutcome(session, finalOutcome, finalReason);
    }

    /// <summary>
    /// Marks this task as semantically interrupted without forcing lifecycle completion, so any active descendant work
    /// can finish unwinding naturally. Ancestor refresh is handled by the session-level caller.
    /// </summary>
    internal void Interrupt(ExecutionSession session, string? statusReason = null)
    {
        if (State == ExecutionTaskState.Completed)
        {
            throw new InvalidOperationException($"Task '{Id}' cannot be interrupted after it already completed.");
        }

        if (!HasStarted)
        {
            throw new InvalidOperationException($"Task '{Id}' cannot be interrupted before execution has started.");
        }

        TransitionOutcome(ExecutionTaskOutcome.Interrupted, statusReason);
        session.NotifyTaskChanged(Id);
    }

    /// <summary>
    /// Validates and applies a lifecycle state transition on this task. Returns false when the transition is a no-op
    /// because the task already has the requested state and reason.
    /// </summary>
    internal bool TransitionState(ExecutionSession session, ExecutionTaskState state, string? statusReason)
    {
        string normalizedReason = statusReason ?? string.Empty;
        if (State == state && string.Equals(StatusReason, normalizedReason, StringComparison.Ordinal))
        {
            return false;
        }

        ValidateStateTransition(session, state);
        ValidateObservedState(state, Outcome);
        (DateTimeOffset? startedAt, DateTimeOffset? finishedAt) = ResolveTaskTiming(state);
        ApplyRuntimeState(state, normalizedReason, Outcome, startedAt, finishedAt);
        return true;
    }

    /// <summary>
    /// Validates and applies a semantic outcome transition on this task. Returns false when the transition is a no-op.
    /// </summary>
    internal bool TransitionOutcome(ExecutionTaskOutcome? outcome, string? statusReason = null)
    {
        string normalizedReason = statusReason ?? string.Empty;
        if (Outcome == outcome && (statusReason == null || string.Equals(StatusReason, normalizedReason, StringComparison.Ordinal)))
        {
            return false;
        }

        ValidateOutcomeTransition(outcome);
        string effectiveReason = statusReason != null ? normalizedReason : StatusReason;
        ValidateObservedState(State, outcome);
        ApplyRuntimeState(State, effectiveReason, outcome, StartedAt, FinishedAt);
        return true;
    }

    /// <summary>
    /// Validates and applies a combined lifecycle state and semantic outcome transition as a single atomic update so
    /// observers never see torn state between lifecycle and outcome fields. Returns false when the transition is a no-op.
    /// </summary>
    internal bool TransitionSnapshot(ExecutionSession session, ExecutionTaskState state, ExecutionTaskOutcome? outcome, string? statusReason)
    {
        string normalizedReason = statusReason ?? string.Empty;
        if (State == state && Outcome == outcome && string.Equals(StatusReason, normalizedReason, StringComparison.Ordinal))
        {
            return false;
        }

        ValidateStateTransition(session, state);
        ValidateOutcomeTransition(outcome);
        ValidateObservedState(state, outcome);
        (DateTimeOffset? startedAt, DateTimeOffset? finishedAt) = ResolveTaskTiming(state);
        ApplyRuntimeState(state, normalizedReason, outcome, startedAt, finishedAt);
        return true;
    }

}
