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
/// Immutable authored specification for one execution task. Captures identity, behavior, and graph structure as
/// authored during plan building. Runtime lifecycle state (execution progress, timing, outcome) is not included.
/// The record's <c>with</c> syntax provides type-safe selective overrides for cloning and child-plan import without
/// manually enumerating every property.
/// </summary>
internal record TaskSpec(
    ExecutionTaskId Id,
    string Title,
    Operation Operation,
    string Description,
    ExecutionTaskId? ParentId,
    IReadOnlyList<ExecutionTaskId> Dependencies,
    bool Enabled,
    string DisabledReason,
    OperationParameters OperationParameters,
    IReadOnlyList<Type> DeclaredOptionTypes,
    Func<ExecutionTaskContext, Task<OperationResult>>? ExecuteAsync,
    bool IsOperationRoot,
    bool IsHiddenInGraph);

/// <summary>
/// Represents one execution-graph node. Authored plans and live sessions share this type, but runtime sessions own their
/// own cloned task instances so mutable graph state, runtime state, and task-scoped logs never leak back into preview
/// plan objects.
/// </summary>
public class ExecutionTask : INotifyPropertyChanged
{
    /* Authored specification bundled into one record so cloning uses `with` syntax instead of manual property
       enumeration. Builder setters update this field via `_spec = _spec with { ... }`. */
    private TaskSpec _spec;

    /* Runtime-mutable fields that are not part of the authored specification. These track live execution progress
       and are reset independently when sessions clone tasks or reinitialize for a new run. */
    private ExecutionTask? _parent;
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
    /// Creates one execution task from an authored specification record. Sessions later clone these tasks into live
    /// nodes and initialize their mutable runtime state independently.
    /// </summary>
    internal ExecutionTask(TaskSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Title))
        {
            throw new ArgumentException("Execution task title is required.", nameof(spec));
        }

        _ = spec.Operation ?? throw new ArgumentNullException(nameof(spec));
        _spec = spec;
        _children = new List<ExecutionTask>();
        LogStream = new BufferedLogStream();

        /* Runtime lifecycle and semantic outcome are tracked separately. Disabled tasks start in a completed lifecycle
           state because no scheduler work remains for them, while their semantic outcome still reports Disabled. */
        _state = spec.Enabled ? ExecutionTaskState.Planned : ExecutionTaskState.Completed;
        _statusReason = spec.Enabled ? string.Empty : spec.DisabledReason;
        _outcome = spec.Enabled ? null : ExecutionTaskOutcome.Disabled;
        ResetReachedStates(_state);
    }

    private readonly List<ExecutionTask> _children;
    private Action<ExecutionTaskId>? _onTaskChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Exposes the authored specification so builders and clone operations can read and override individual properties
    /// via <c>with</c> syntax without manually enumerating every field.
    /// </summary>
    internal TaskSpec Spec => _spec;

    public ExecutionTaskId Id => _spec.Id;

    public string Title => _spec.Title;

    public Operation Operation => _spec.Operation;

    public string Description => _spec.Description;

    public ExecutionTaskId? ParentId
    {
        get => _parent?.Id ?? _spec.ParentId;
        internal set
        {
            if (Equals(_spec.ParentId, value))
            {
                return;
            }

            _spec = _spec with { ParentId = value };
            RaisePropertyChanged();
        }
    }

    /// <summary>
    /// Gets the direct parent task in the execution tree. Null for the root task.
    /// </summary>
    public ExecutionTask? Parent => _parent;

    /// <summary>
    /// Gets the IDs of direct dependency tasks that must complete before this task can start.
    /// </summary>
    public IReadOnlyList<ExecutionTaskId> Dependencies => _spec.Dependencies;

    /// <summary>
    /// Gets the IDs of direct child tasks.
    /// </summary>
    public IReadOnlyList<ExecutionTaskId> ChildTaskIds => _children.Select(child => child.Id).ToList().AsReadOnly();

    /// <summary>
    /// Gets the direct child tasks in the execution tree. Populated during session initialization and runtime child
    /// merging via <see cref="AddChild"/>.
    /// </summary>
    public IReadOnlyList<ExecutionTask> Children => _children.AsReadOnly();

    public bool Enabled => _spec.Enabled;

    public string DisabledReason => _spec.DisabledReason;

    public OperationParameters OperationParameters => _spec.OperationParameters;

    public IReadOnlyList<Type> DeclaredOptionTypes => _spec.DeclaredOptionTypes;

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
    public bool IsOperationRoot => _spec.IsOperationRoot;

    /// <summary>
    /// Gets whether this task should be collapsed out of the graph projection unless the UI is configured to reveal
    /// hidden tasks.
    /// </summary>
    public bool IsHiddenInGraph => _spec.IsHiddenInGraph;

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

    /// <summary>
    /// Clones this task for a live session by preserving the full authored specification, including dependency IDs.
    /// </summary>
    internal ExecutionTask CloneForSession()
    {
        return CloneWith(_spec);
    }

    /// <summary>
    /// Creates one task clone from a (potentially overridden) specification. Callers use <c>with</c> syntax on
    /// <see cref="Spec"/> to override individual properties:
    /// <c>task.CloneWith(task.Spec with { ParentId = newParent, Title = newTitle })</c>.
    /// </summary>
    internal ExecutionTask CloneWith(TaskSpec spec)
    {
        return new ExecutionTask(spec);
    }

    /// <summary>
    /// Returns the current scheduler-facing start state for this task subtree. Walks children and dependencies via
    /// direct object references without requiring external session lookup.
    /// </summary>
    internal TaskStartState GetTaskStartState()
    {
        if (State == ExecutionTaskState.Completed)
        {
            return TaskStartState.NoStartableWork;
        }

        if (CanStartOwnWork())
        {
            return TaskStartState.Ready;
        }

        if (_children.Any(child => child.GetTaskStartState() == TaskStartState.Ready))
        {
            return TaskStartState.Ready;
        }

        if ((_spec.ExecuteAsync != null && State == ExecutionTaskState.Running) || _children.Any(child => child.GetTaskStartState() == TaskStartState.Running))
        {
            return TaskStartState.Running;
        }

        if (_spec.ExecuteAsync != null && State == ExecutionTaskState.Pending)
        {
            if (!AreDependenciesSatisfied())
            {
                return TaskStartState.WaitingForDependencies;
            }

            if (!AreAncestorsOpen())
            {
                return TaskStartState.WaitingForParent;
            }
        }

        if (_children.Any(child => child.GetTaskStartState() == TaskStartState.WaitingForDependencies))
        {
            return TaskStartState.WaitingForDependencies;
        }

        if (_children.Any(child => child.GetTaskStartState() == TaskStartState.WaitingForParent))
        {
            return TaskStartState.WaitingForParent;
        }

        return TaskStartState.NoStartableWork;
    }

    /// <summary>
    /// Returns the scheduler-ready branch roots beneath this task. A task with ready descendants contributes those
    /// descendant branch roots directly instead of surfacing only itself, which keeps independently ready sibling
    /// branches startable even when another sibling subtree has already inserted deeper ready work.
    /// </summary>
    internal IReadOnlyList<ExecutionTask> GetSchedulerReadyBranchRoots()
    {
        if (State == ExecutionTaskState.Completed)
        {
            return Array.Empty<ExecutionTask>();
        }

        List<ExecutionTask> readyChildBranches = _children
            .SelectMany(child => child.GetSchedulerReadyBranchRoots())
            .ToList();

        if (CanStartOwnWork())
        {
            if (_children.Count == 0)
            {
                return new[] { this };
            }

            /* A task that can start its own body while also containing ready descendants still represents one scheduler
               branch root. Starting the task body may itself produce more ready work, but that should not suppress the
               descendant branch roots of sibling subtrees. */
            readyChildBranches.Insert(0, this);
            return readyChildBranches;
        }

        return readyChildBranches;
    }

    /// <summary>
    /// Returns the execution locks required for the next startable work item in this task subtree.
    /// </summary>
    /// <summary>
    /// Returns the execution locks required for the next startable work item in this task subtree.
    /// </summary>
    internal IReadOnlyList<ExecutionLock> GetExecutionLocksForNextStart()
    {
        foreach (ExecutionTask child in _children)
        {
            if (child.GetTaskStartState() == TaskStartState.Ready)
            {
                return child.GetExecutionLocksForNextStart();
            }
        }

        if (CanStartOwnWork())
        {
            return Operation.GetDeclaredExecutionLocks(new ValidatedOperationParameters(Title, OperationParameters, DeclaredOptionTypes));
        }

        return Array.Empty<ExecutionLock>();
    }

    /// <summary>
    /// Starts the next startable work item in this task subtree and returns the actual task that entered Running.
    /// Walks children via direct object references.
    /// </summary>
    internal TaskStartResult StartAsync(ExecutionTaskContext context)
    {
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        ExecutionTaskRuntimeServices runtime = context.GetRequiredRuntime();

        foreach (ExecutionTask child in _children)
        {
            if (child.GetTaskStartState() == TaskStartState.Ready)
            {
                return child.StartAsync(context.CreateForTask(child));
            }
        }

        if (!CanStartOwnWork())
        {
            throw new InvalidOperationException($"Task '{Id}' does not have any work ready to start.");
        }

        runtime.SetTaskState(Id, ExecutionTaskState.Running);
        return new TaskStartResult(this, _spec.ExecuteAsync!(context));
    }

    /// <summary>
    /// Adds one child task to this task and wires the parent-child object references so the tree can be navigated
    /// directly without external lookup. Deduplicates by task identity.
    /// </summary>
    internal void AddChild(ExecutionTask child)
    {
        if (child == null)
        {
            throw new ArgumentNullException(nameof(child));
        }

        if (_children.Any(existing => existing.Id == child.Id))
        {
            return;
        }

        _children.Add(child);
        child._parent = this;
        RaisePropertyChanged(nameof(Children));
        RaisePropertyChanged(nameof(ChildTaskIds));
    }

    internal void InitializeRuntimeState(bool hasBegunExecution, Action<ExecutionTaskId> onTaskChanged)
    {
        _onTaskChanged = onTaskChanged ?? throw new ArgumentNullException(nameof(onTaskChanged));

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

    /// <summary>
    /// Adds one dependency on another task that must complete before this task can start. Deduplicates by task identity.
    /// Called during plan authoring when the builder wires sequencing frontiers.
    /// </summary>
    internal void AddDependency(ExecutionTaskId dependencyId)
    {
        if (Dependencies.Contains(dependencyId))
        {
            return;
        }

        List<ExecutionTaskId> dependencies = Dependencies.ToList();
        dependencies.Add(dependencyId);
        _spec = _spec with { Dependencies = dependencies.AsReadOnly() };
        RaisePropertyChanged(nameof(Dependencies));
    }

    /// <summary>
    /// Updates builder-owned presentation metadata before the task graph becomes a live runtime session.
    /// </summary>
    internal void SetDescription(string description)
    {
        _spec = _spec with { Description = description ?? string.Empty };
    }

    /// <summary>
    /// Updates whether the task participates in the authored plan and the user-facing reason when it does not.
    /// </summary>
    internal void SetCondition(bool enabled, string? disabledReason)
    {
        _spec = _spec with
        {
            Enabled = enabled,
            DisabledReason = enabled ? string.Empty : (disabledReason ?? string.Empty)
        };
    }

    /// <summary>
    /// Assigns the validated parameter context that builder-authored execution will use at runtime.
    /// </summary>
    internal void SetOperationParameters(OperationParameters operationParameters, IReadOnlyList<Type> declaredOptionTypes)
    {
        _spec = _spec with
        {
            OperationParameters = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters)),
            DeclaredOptionTypes = declaredOptionTypes ?? throw new ArgumentNullException(nameof(declaredOptionTypes))
        };
    }

    /// <summary>
    /// Controls whether this authored task is hidden in the graph projection until debugging reveals internal nodes.
    /// </summary>
    internal void SetHiddenInGraph(bool isHiddenInGraph)
    {
        _spec = _spec with { IsHiddenInGraph = isHiddenInGraph };
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
    /// Returns whether all direct dependencies for this task are satisfied strongly enough for downstream work to start.
    /// Resolves dependency IDs by walking the tree from the root. The O(n) walk per dependency is acceptable for trees
    /// of 10-100 nodes and keeps dependency storage in authored task specs instead of maintaining cached object
    /// references.
    /// </summary>
    internal bool AreDependenciesSatisfied()
    {
        if (Dependencies.Count == 0)
        {
            return true;
        }

        ExecutionTask root = GetTreeRoot();
        return Dependencies.All(id => IsDependencySatisfied(root, id));
    }

    /// <summary>
    /// Returns whether one dependency task is satisfied strongly enough for downstream work to start. Disabled
    /// dependencies are treated as satisfied when their own transitive dependencies are also satisfied.
    /// </summary>
    private static bool IsDependencySatisfied(ExecutionTask root, ExecutionTaskId dependencyId)
    {
        ExecutionTask dependencyTask = root.FindTask(dependencyId)
            ?? throw new InvalidOperationException($"Dependency task '{dependencyId}' not found in the execution tree.");

        if (dependencyTask.Outcome == ExecutionTaskOutcome.Completed)
        {
            return true;
        }

        if (dependencyTask.Outcome != ExecutionTaskOutcome.Disabled)
        {
            return false;
        }

        /* Disabled dependencies pass through to their own dependencies so the scheduler treats a chain of disabled
           tasks as satisfied only when the entire transitive chain has completed or is disabled-through. */
        return dependencyTask.Dependencies.All(id => IsDependencySatisfied(root, id));
    }

    /// <summary>
    /// Returns whether this task's directly attached runnable work can start immediately.
    /// </summary>
    internal bool CanStartOwnWork()
    {
        return _spec.ExecuteAsync != null
            && State == ExecutionTaskState.Pending
            && AreDependenciesSatisfied()
            && AreAncestorsOpen();
    }

    /// <summary>
    /// Returns whether every ancestor container for this task is structurally open, meaning none have completed or been
    /// assigned a doomed outcome, and all ancestor dependencies are satisfied.
    /// </summary>
    internal bool AreAncestorsOpen()
    {
        ExecutionTask? currentTask = _parent;
        while (currentTask != null)
        {
            if (currentTask.State == ExecutionTaskState.Completed || currentTask.Outcome != null)
            {
                return false;
            }

            if (!currentTask.AreDependenciesSatisfied())
            {
                return false;
            }

            currentTask = currentTask._parent;
        }

        return true;
    }

    /// <summary>
    /// Returns the current scheduler-facing pending reason for this task, or null when the task is ready to start.
    /// </summary>
    /// <summary>
    /// Returns the current scheduler-facing pending reason for this task, or null when the task is ready to start.
    /// </summary>
    internal string? GetSchedulingPendingReason()
    {
        if (State != ExecutionTaskState.Pending)
        {
            return null;
        }

        TaskStartState startState = GetTaskStartState();
        if (startState == TaskStartState.WaitingForDependencies)
        {
            return "Waiting for dependencies.";
        }

        return startState == TaskStartState.WaitingForParent
            ? "Waiting for parent scope."
            : null;
    }

    /// <summary>
    /// Returns whether this task has reached a terminal runtime state.
    /// </summary>
    internal bool IsTerminal => State == ExecutionTaskState.Completed;

    /// <summary>
    /// Walks up the parent chain to find the root of the execution tree. Used by dependency resolution to locate
    /// sibling and cousin tasks by ID without needing an external session reference.
    /// </summary>
    private ExecutionTask GetTreeRoot()
    {
        ExecutionTask current = this;
        while (current._parent != null)
        {
            current = current._parent;
        }

        return current;
    }

    /// <summary>
    /// Searches this task and its entire subtree for a task with the given id. Returns null when no match exists beneath
    /// this node. Used by session-level GetTask as the tree-walk replacement for the former dictionary lookup.
    /// </summary>
    internal ExecutionTask? FindTask(ExecutionTaskId taskId)
    {
        if (Id == taskId)
        {
            return this;
        }

        foreach (ExecutionTask child in _children)
        {
            ExecutionTask? found = child.FindTask(taskId);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Collects this task and all descendants into a flat list by walking the tree recursively. The order is pre-order
    /// depth-first so parents appear before their children.
    /// </summary>
    internal IReadOnlyList<ExecutionTask> GetAllTasks()
    {
        List<ExecutionTask> allTasks = new();
        CollectAllTasks(allTasks);
        return allTasks;
    }

    /// <summary>
    /// Recursively collects this task and all descendants into the provided list.
    /// </summary>
    private void CollectAllTasks(List<ExecutionTask> destination)
    {
        destination.Add(this);
        foreach (ExecutionTask child in _children)
        {
            child.CollectAllTasks(destination);
        }
    }

    /// <summary>
    /// Returns this task's id plus every descendant id beneath it in the execution tree.
    /// </summary>
    internal IReadOnlyList<ExecutionTaskId> GetSubtreeIds()
    {
        List<ExecutionTaskId> subtreeIds = new() { Id };
        CollectDescendantIds(subtreeIds);
        return subtreeIds;
    }

    /// <summary>
    /// Recursively collects all descendant task ids beneath this task into the provided collection.
    /// </summary>
    private void CollectDescendantIds(ICollection<ExecutionTaskId> descendantTaskIds)
    {
        foreach (ExecutionTask child in _children)
        {
            descendantTaskIds.Add(child.Id);
            child.CollectDescendantIds(descendantTaskIds);
        }
    }

    /// <summary>
    /// Builds a human-readable task path from the current live parent chain so logs can identify this task by its visible
    /// location in the execution tree instead of only by its generated id.
    /// </summary>
    internal string GetDisplayPath()
    {
        List<string> segments = new();
        ExecutionTask? currentTask = this;
        while (currentTask != null)
        {
            segments.Add(currentTask.Title);
            currentTask = currentTask._parent;
        }

        segments.Reverse();
        return string.Join(" > ", segments);
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
    /// Guards lifecycle transitions so execution state stays monotonic once a task or container subtree has started.
    /// </summary>
    /// <summary>
    /// Guards lifecycle transitions so execution state stays monotonic once a task or container subtree has started.
    /// </summary>
    internal void ValidateStateTransition(ExecutionTaskState nextState)
    {
        if (State == ExecutionTaskState.Completed && nextState != ExecutionTaskState.Completed)
        {
            throw new InvalidOperationException($"Task '{Id}' cannot transition from completed execution state back to '{nextState}'.");
        }

        if (State == ExecutionTaskState.Running && nextState is ExecutionTaskState.Pending or ExecutionTaskState.Planned)
        {
            /* Once a task or any descendant in its subtree has started, queued states are no longer legal. Container
               scopes must remain Running until they complete or fail, even if later descendant work is only waiting on
               dependencies or parent readiness. */
            throw new InvalidOperationException($"Task '{Id}' cannot transition from running back to queued state '{nextState}'.");
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
    internal void ValidateTerminalAssignment(ExecutionTaskOutcome outcome)
    {
        if (outcome == ExecutionTaskOutcome.Completed && HasNonTerminalDescendants())
        {
            throw new InvalidOperationException($"Task '{Title}' ({Id}) cannot complete successfully while it still has non-terminal descendants.");
        }

        if (outcome == ExecutionTaskOutcome.Skipped && HasStartedWorkInSubtree())
        {
            throw new InvalidOperationException($"Task '{Title}' ({Id}) cannot be marked skipped after execution has started in its subtree.");
        }

        if (outcome == ExecutionTaskOutcome.Interrupted && !HasStartedWorkInSubtree())
        {
            throw new InvalidOperationException($"Task '{Title}' ({Id}) cannot be marked interrupted before execution has started in its subtree.");
        }
    }

    /// <summary>
    /// Returns whether any task in this subtree has already transitioned out of its untouched planned or pending state.
    /// </summary>
    internal bool HasStartedWorkInSubtree()
    {
        if (HasStarted)
        {
            return true;
        }

        return _children.Any(child => child.HasStartedWorkInSubtree());
    }

    /// <summary>
    /// Returns whether any descendant beneath this task is still pending or running.
    /// </summary>
    internal bool HasNonTerminalDescendants()
    {
        return _children.Any(child => !child.IsTerminal || child.HasNonTerminalDescendants());
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
    /// Fires the task-changed notification through the callback wired during runtime initialization. This decouples
    /// mutation methods from the session so they operate on the task's own state and notify observers without needing
    /// a direct session reference.
    /// </summary>
    private void RaiseTaskChanged()
    {
        if (_onTaskChanged == null)
        {
            throw new InvalidOperationException($"Task '{Id}' has no task-changed callback. Was InitializeRuntimeState called?");
        }

        _onTaskChanged(Id);
    }

    /// <summary>
    /// Skips every unfinished task in this subtree without disturbing tasks that already reached terminal states. Each
    /// skipped task validates its terminal assignment, transitions to completed, propagates to its own descendants, and
    /// raises the task-changed notification so events fire from the authoritative event surface.
    /// </summary>
    internal void SkipUnfinishedSubtree(string? reason, ExecutionTaskOutcome skippedOutcome = ExecutionTaskOutcome.Skipped)
    {
        if (State is ExecutionTaskState.Planned or ExecutionTaskState.Pending)
        {
            CompleteWithOutcome(skippedOutcome, reason);
        }

        foreach (ExecutionTask child in _children)
        {
            child.SkipUnfinishedSubtree(reason, skippedOutcome);
        }
    }

    /// <summary>
    /// Skips every unfinished descendant beneath this task by walking each direct child subtree.
    /// </summary>
    internal void SkipUnfinishedDescendants(string? reason, ExecutionTaskOutcome skippedOutcome = ExecutionTaskOutcome.Skipped)
    {
        foreach (ExecutionTask child in _children)
        {
            child.SkipUnfinishedSubtree(reason, skippedOutcome);
        }
    }

    /// <summary>
    /// Marks one started subtree as interrupted after sibling failure. Started nodes keep their current lifecycle so any
    /// active descendant work can finish unwinding, while untouched nodes become skipped because they never ran.
    /// </summary>
    internal void MarkSubtreeInterrupted(string? reason)
    {
        if (State != ExecutionTaskState.Completed)
        {
            if (HasStarted)
            {
                /* Started tasks receive an interrupted outcome without forcing lifecycle completion so any active
                   descendant work can finish unwinding naturally. */
                TransitionOutcome(ExecutionTaskOutcome.Interrupted, reason);
                RaiseTaskChanged();
            }
            else
            {
                CompleteWithOutcome(ExecutionTaskOutcome.Skipped, reason);
            }
        }

        foreach (ExecutionTask child in _children)
        {
            child.MarkSubtreeInterrupted(reason);
        }
    }

    /// <summary>
    /// Interrupts one sibling subtree after a different sibling has failed. Subtrees that already started work surface as
    /// Interrupted, while untouched subtrees remain Skipped.
    /// </summary>
    internal void InterruptSiblingSubtree(ExecutionTaskId failedSiblingRootId)
    {
        bool subtreeStarted = HasStartedWorkInSubtree();
        string reason = subtreeStarted
            ? $"Interrupted because sibling task '{failedSiblingRootId}' failed."
            : $"Skipped because sibling task '{failedSiblingRootId}' failed.";

        if (subtreeStarted)
        {
            MarkSubtreeInterrupted(reason);
            return;
        }

        SkipUnfinishedSubtree(reason, ExecutionTaskOutcome.Skipped);
    }

    /// <summary>
    /// Propagates a terminal outcome to untouched descendants so tasks that never started inherit a completed lifecycle
    /// plus the semantic outcome that explains why they will never run.
    /// </summary>
    internal void PropagateTerminalOutcomeToUntouchedDescendants(ExecutionTaskOutcome outcome, string? statusReason)
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
        SkipUnfinishedDescendants(descendantReason, descendantOutcome);
    }

    /// <summary>
    /// Completes this task's lifecycle and assigns its semantic outcome, then propagates to untouched descendants. The
    /// task-changed callback fires so the externally visible event surface stays consistent. Ancestor refresh is handled
    /// by the session-level caller since it requires cross-branch coordination.
    /// </summary>
    internal void CompleteWithOutcome(ExecutionTaskOutcome outcome, string? statusReason = null)
    {
        ValidateTerminalAssignment(outcome);
        TransitionSnapshot(ExecutionTaskState.Completed, outcome, statusReason);
        RaiseTaskChanged();
        PropagateTerminalOutcomeToUntouchedDescendants(outcome, statusReason);
    }

    /// <summary>
    /// Completes this task's lifecycle while preserving any previously assigned doomed outcome (failed, cancelled, or
    /// interrupted) that was recorded while descendant work was still unwinding. When no doomed outcome exists, the
    /// provided success outcome is used instead.
    /// </summary>
    internal void CompleteLifecycle(ExecutionTaskOutcome successOutcome = ExecutionTaskOutcome.Completed, string? statusReason = null)
    {
        ExecutionTaskOutcome finalOutcome = Outcome is ExecutionTaskOutcome.Failed or ExecutionTaskOutcome.Cancelled or ExecutionTaskOutcome.Interrupted
            ? Outcome.Value
            : successOutcome;
        string? finalReason = finalOutcome == successOutcome ? statusReason : StatusReason;
        CompleteWithOutcome(finalOutcome, finalReason);
    }

    /// <summary>
    /// Marks this task as semantically interrupted without forcing lifecycle completion, so any active descendant work
    /// can finish unwinding naturally. Ancestor refresh is handled by the session-level caller.
    /// </summary>
    internal void Interrupt(string? statusReason = null)
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
        RaiseTaskChanged();
    }

    /// <summary>
    /// Validates and applies a lifecycle state transition on this task. Returns false when the transition is a no-op
    /// because the task already has the requested state and reason.
    /// </summary>
    internal bool TransitionState(ExecutionTaskState state, string? statusReason)
    {
        string normalizedReason = statusReason ?? string.Empty;
        if (State == state && string.Equals(StatusReason, normalizedReason, StringComparison.Ordinal))
        {
            return false;
        }

        ValidateStateTransition(state);
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
    internal bool TransitionSnapshot(ExecutionTaskState state, ExecutionTaskOutcome? outcome, string? statusReason)
    {
        string normalizedReason = statusReason ?? string.Empty;
        if (State == state && Outcome == outcome && string.Equals(StatusReason, normalizedReason, StringComparison.Ordinal))
        {
            return false;
        }

        ValidateStateTransition(state);
        ValidateOutcomeTransition(outcome);
        ValidateObservedState(state, outcome);
        (DateTimeOffset? startedAt, DateTimeOffset? finishedAt) = ResolveTaskTiming(state);
        ApplyRuntimeState(state, normalizedReason, outcome, startedAt, finishedAt);
        return true;
    }

}
