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
    AwaitingDependency = 1,
    WaitingForDependencies = AwaitingDependency,
    AwaitingLock,
    WaitingForExecutionLock = AwaitingLock,
    Running,
    NoStartableWork
}

internal sealed class TaskStartResult
{
    /// <summary>
    /// Carries one task that has already been started so callers can await the running task directly.
    /// </summary>
    public TaskStartResult(ExecutionTask task, Task<OperationResult> runningTask)
    {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        RunningTask = runningTask ?? throw new ArgumentNullException(nameof(runningTask));
    }

    public ExecutionTask Task { get; }

    /// <summary>
    /// Gets the already-started task.
    /// </summary>
    public Task<OperationResult> RunningTask { get; }
}

internal delegate Task<OperationResult> TaskExecutionRunner(ExecutionTask task, Func<Task<OperationResult>> executeAsync);

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
    IReadOnlyList<ExecutionLock> DeclaredExecutionLocks,
    Func<ExecutionTaskContext, Task<OperationResult>>? ExecuteAsync,
    bool IsOperationRoot,
    bool IsHiddenInGraph);

/// <summary>
/// Represents one execution-graph node. Authored plans and live sessions share this type, but runtime sessions own their
/// own cloned task instances so all per-task runtime data — lifecycle state, subtree metrics, graph links, and task-
/// scoped logs — stays on the task itself instead of being mirrored onto the session.
/// </summary>
public class ExecutionTask : INotifyPropertyChanged
{
    /* Authored specification bundled into one record so cloning uses `with` syntax instead of manual property
       enumeration. Builder setters update this field via `_spec = _spec with { ... }`. */
    private TaskSpec _spec;

    /* Runtime-mutable fields that are not part of the authored specification. These track live execution progress and
       task-owned subtree metrics, and are reset independently when sessions clone tasks or reinitialize for a new run.
       Sessions coordinate updates under the graph lock, but the per-task data itself lives here on the task. */
    private ExecutionTask? _parent;
    private ExecutionTaskState _state;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;
    private ExecutionTaskOutcome? _outcome;
    private int _subtreeWarningCount;
    private int _subtreeErrorCount;
    private int _subtreeActiveTimingCount;
    private DateTimeOffset? _subtreeStartedAt;
    private DateTimeOffset? _subtreeFinishedAt;
    /* Cached ancestor-independent scheduler frontier for this subtree. Ancestor gating stays outside this cache so the
       scheduler can update local subtree readiness once and apply scope-open checks only where it actually matters. */
    private TaskStartState _subtreeStartState;
    private readonly object _activeExecutionSyncRoot = new();
    private Task<OperationResult>? _activeExecutionTask;
    private readonly Dictionary<Type, object> _dataByType = new();
    /* Guards the atomic "subscribe then inspect current state" path used by task-local state waiters so they can rely
       directly on the task's own StateChanged event without a second waiter registry. */
    private readonly object _stateChangeSyncRoot = new();

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
        _observedDependencies = new List<ExecutionTask>();
        LogStream = new BufferedLogStream();

        /* Runtime lifecycle and semantic outcome are tracked separately. Disabled tasks start in a completed lifecycle
           state because no scheduler work remains for them, while their semantic outcome still reports Disabled. */
        _state = spec.Enabled ? ExecutionTaskState.Planned : ExecutionTaskState.Completed;
        _outcome = spec.Enabled ? null : ExecutionTaskOutcome.Disabled;
        ResetSubtreeMetrics();
        ResetSubtreeSchedulingRollup();
    }

    private readonly List<ExecutionTask> _children;
    /* Runtime dependency observation stays on the task itself so dependency-driven readiness updates can flow directly to
       the affected task instead of requiring a session-wide sweep to rediscover which pending nodes changed. */
    private readonly List<ExecutionTask> _observedDependencies;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised whenever this task's task-owned runtime facts change. The event carries the task plus the current visible
    /// state snapshot so waiters, session fanout, and internal propagation can all share one task event without re-reading
    /// state unless they want to.
    /// </summary>
    public event Action<ExecutionTask, ExecutionTaskState, ExecutionTaskOutcome?>? StateChanged;

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
    /// Gets any execution locks authored directly on this task body. An empty list means the task falls back to the
    /// owning operation's declared locks instead.
    /// </summary>
    public IReadOnlyList<ExecutionLock> DeclaredExecutionLocks => _spec.DeclaredExecutionLocks;

    /// <summary>
    /// Gets the current execution lifecycle status for this task scope.
    /// </summary>
    public ExecutionTaskState State
    {
        get => _state;
        internal set => SetProperty(ref _state, value);
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
    /// Gets the live execution task currently owned by this task, if any. This is separate from lifecycle state so the
    /// scheduler can await and identify active body executions without keeping a parallel ownership registry.
    /// </summary>
    internal Task<OperationResult>? ActiveExecutionTask
    {
        get
        {
            lock (_activeExecutionSyncRoot)
            {
                return _activeExecutionTask;
            }
        }
    }

    /// <summary>
    /// Returns whether this task currently owns a live execution task handle.
    /// </summary>
    internal bool HasActiveExecution
    {
        get
        {
            lock (_activeExecutionSyncRoot)
            {
                return _activeExecutionTask != null;
            }
        }
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

    /// <summary>
    /// Returns whether this authored task already owns a directly attached Run(...) body.
    /// Builder-time validation uses this to keep one task from mixing a static authored subtree with direct body work.
    /// </summary>
    internal bool HasAuthoredBody => _spec.ExecuteAsync != null;

    /// <summary>
    /// Gets the buffered direct log stream owned by this task body.
    /// </summary>
    public BufferedLogStream LogStream { get; }

    /// <summary>
    /// Resets the task-owned subtree metric basis for a fresh runtime session.
    /// </summary>
    internal void ResetSubtreeMetrics()
    {
        _subtreeWarningCount = 0;
        _subtreeErrorCount = 0;
        _subtreeActiveTimingCount = 0;
        _subtreeStartedAt = null;
        _subtreeFinishedAt = null;
    }

    /// <summary>
    /// Resets the task-owned scheduler rollup that summarizes the subtree's current scheduler frontier.
    /// </summary>
    internal void ResetSubtreeSchedulingRollup()
    {
        _subtreeStartState = TaskStartState.NoStartableWork;
    }

    /// <summary>
    /// Applies one warning/error delta to this task's cached subtree counts.
    /// </summary>
    internal void ApplySubtreeLogDelta(int warningDelta, int errorDelta)
    {
        _subtreeWarningCount += warningDelta;
        _subtreeErrorCount += errorDelta;
        if (_subtreeWarningCount < 0 || _subtreeErrorCount < 0)
        {
            throw new InvalidOperationException($"Task '{Id}' subtree log counts cannot become negative.");
        }
    }

    /// <summary>
    /// Clears the cached subtree warning/error counts for this task.
    /// </summary>
    internal void ResetSubtreeLogCounts()
    {
        _subtreeWarningCount = 0;
        _subtreeErrorCount = 0;
    }

    /// <summary>
    /// Recomputes the cached subtree timing basis from this task's own timestamps plus its direct children's cached
    /// subtree timing basis.
    /// </summary>
    internal void RecomputeSubtreeTimingMetrics()
    {
        int activeTimingCount = StartedAt != null && FinishedAt == null ? 1 : 0;
        DateTimeOffset? subtreeStartedAt = StartedAt;
        DateTimeOffset? subtreeFinishedAt = FinishedAt;

        foreach (ExecutionTask child in _children)
        {
            activeTimingCount += child._subtreeActiveTimingCount;
            if (child._subtreeStartedAt != null && (subtreeStartedAt == null || child._subtreeStartedAt < subtreeStartedAt))
            {
                subtreeStartedAt = child._subtreeStartedAt;
            }

            if (child._subtreeFinishedAt != null && (subtreeFinishedAt == null || child._subtreeFinishedAt > subtreeFinishedAt))
            {
                subtreeFinishedAt = child._subtreeFinishedAt;
            }
        }

        _subtreeActiveTimingCount = activeTimingCount;
        _subtreeStartedAt = subtreeStartedAt;
        _subtreeFinishedAt = subtreeFinishedAt;
    }

    /// <summary>
    /// Returns the cached subtree metrics snapshot for this task using the supplied clock when the subtree still has
    /// started-but-unfinished work.
    /// </summary>
    internal ExecutionTaskMetrics GetSubtreeMetrics(DateTimeOffset? now = null)
    {
        return new ExecutionTaskMetrics(GetSubtreeDuration(now), _subtreeWarningCount, _subtreeErrorCount);
    }

    /// <summary>
    /// Returns the cached subtree duration for this task using the supplied clock when the subtree has started work that
    /// has not yet completed.
    /// </summary>
    internal TimeSpan? GetSubtreeDuration(DateTimeOffset? now = null)
    {
        if (_subtreeStartedAt == null)
        {
            return null;
        }

        DateTimeOffset resolvedEnd = _subtreeActiveTimingCount > 0
            ? now ?? DateTimeOffset.Now
            : _subtreeFinishedAt ?? now ?? DateTimeOffset.Now;
        TimeSpan duration = resolvedEnd - _subtreeStartedAt.Value;
        return duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
    }

    /// <summary>
    /// Recomputes the cached scheduler rollup for this task from one local own-work state plus the already-cached subtree
    /// start states on its direct children. Ancestor gating is intentionally excluded because schedulability through a
    /// closed parent scope is derived when callers evaluate the current live ancestor chain.
    /// </summary>
    internal bool RecomputeSubtreeSchedulingRollup()
    {
        TaskStartState nextSubtreeStartState = GetOwnWorkStartStateIgnoringAncestors();
        foreach (ExecutionTask child in _children)
        {
            nextSubtreeStartState = CombineSubtreeStartStates(nextSubtreeStartState, child._subtreeStartState);
        }

        bool changed = nextSubtreeStartState != _subtreeStartState;
        _subtreeStartState = nextSubtreeStartState;
        return changed;
    }

    /// <summary>
    /// Waits until this task is currently in the requested runtime state or transitions into it later during the current
    /// session lifetime.
    /// </summary>
    public Task WaitForStateAsync(ExecutionTaskState targetState, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        TaskCompletionSource<bool> completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration cancellationRegistration = default;
        Action<ExecutionTask, ExecutionTaskState, ExecutionTaskOutcome?>? handler = null;

        void Cleanup()
        {
            lock (_stateChangeSyncRoot)
            {
                if (handler != null)
                {
                    StateChanged -= handler;
                }
            }

            cancellationRegistration.Dispose();
        }

        void CompleteSuccess()
        {
            Cleanup();
            completionSource.TrySetResult(true);
        }

        void CompleteFailure(ExecutionTaskState? target)
        {
            Cleanup();
            completionSource.TrySetException(CreateUnreachableStateException(target));
        }

        handler = (task, state, _) =>
        {
            if (!ReferenceEquals(task, this))
            {
                return;
            }

            if (state == targetState)
            {
                CompleteSuccess();
                return;
            }

            if (state == ExecutionTaskState.Completed)
            {
                CompleteFailure(targetState);
            }
        };

        lock (_stateChangeSyncRoot)
        {
            if (_state == targetState)
            {
                return Task.CompletedTask;
            }

            if (_state == ExecutionTaskState.Completed)
            {
                return Task.FromException(CreateUnreachableStateException(targetState));
            }

            StateChanged += handler;
            if (cancellationToken.CanBeCanceled)
            {
                cancellationRegistration = cancellationToken.Register(() =>
                {
                    Cleanup();
                    completionSource.TrySetCanceled(cancellationToken);
                });
            }
        }

        return completionSource.Task;
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
    /// Returns the current cached scheduler frontier for this task subtree while intentionally ignoring ancestor gating
    /// above this task.
    /// </summary>
    internal TaskStartState GetTaskStartState()
    {
        return _subtreeStartState;
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

        /* Descendant work is only scheduler-ready when this scope itself is open for descendant execution. The cached
           child frontier stays ancestor-independent, so this method applies the current scope gate directly instead of
           synthesizing a separate effective start-state enum value. */
        List<ExecutionTask> readyChildBranches = IsScopeOpenForDescendantWork()
            ? _children
                .Where(child => child.GetTaskStartState() == TaskStartState.Ready)
                .SelectMany(child => child.GetSchedulerReadyBranchRoots())
                .ToList()
            : new List<ExecutionTask>();

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
    /// Resolves the concrete task that would enter Running next for this ready branch by following the same child-first
    /// traversal the scheduler uses to start work.
    /// </summary>
    internal ExecutionTask? TryGetNextStartableTask()
    {
        /* Child-first traversal should only walk into descendants when this scope is currently open for descendant
           execution. Locally ready descendants beneath a closed scope stay pending until the ancestor gate opens. */
        if (IsScopeOpenForDescendantWork())
        {
            foreach (ExecutionTask child in _children)
            {
                if (child.GetTaskStartState() == TaskStartState.Ready)
                {
                    return child.TryGetNextStartableTask();
                }
            }
        }

        return CanStartOwnWork() ? this : null;
    }

    /// <summary>
    /// Returns the concrete task that would enter Running next for this ready branch.
    /// </summary>
    internal ExecutionTask GetNextStartableTask()
    {
        return TryGetNextStartableTask()
            ?? throw new InvalidOperationException($"Task '{Id}' does not have any work ready to start.");
    }

    /// <summary>
    /// Returns the execution locks required for the next startable work item in this task subtree.
    /// </summary>
    internal IReadOnlyList<ExecutionLock> GetExecutionLocksForNextStart()
    {
        ExecutionTask? nextStartTask = TryGetNextStartableTask();
        if (nextStartTask == null)
        {
            return Array.Empty<ExecutionLock>();
        }

        return nextStartTask.GetDeclaredExecutionLocks();
    }

    /// <summary>
    /// Returns the execution locks that the runtime should hold while this specific task body executes. Task-authored
    /// locks take priority so inline builder-authored tasks can express contention surgically, and otherwise the task
    /// falls back to the owning operation's declared lock policy.
    /// </summary>
    internal IReadOnlyList<ExecutionLock> GetDeclaredExecutionLocks()
    {
        if (_spec.DeclaredExecutionLocks.Count > 0)
        {
            return _spec.DeclaredExecutionLocks;
        }

        return Operation.GetDeclaredExecutionLocks(CreateValidatedOperationParameters());
    }

    /// <summary>
    /// Resolves the concrete next startable task in this subtree. Task-local selection stays here with the task, while
    /// the session still owns runtime-context construction and state transitions, and the scheduler still owns when
    /// execution actually begins.
    /// </summary>
    internal ExecutionTask ResolveTaskToStart()
    {
        return GetNextStartableTask();
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
        /* Parents observe the child's task-owned change event directly so child-frontier changes can roll up immediately
           even when the child's visible lifecycle state itself stays unchanged. */
        child.StateChanged += HandleObservedTaskChanged;
        RaisePropertyChanged(nameof(Children));
        RaisePropertyChanged(nameof(ChildTaskIds));
    }

    /// <summary>
    /// Wires one runtime dependency observation edge so this task recomputes itself whenever that dependency changes.
    /// Authored dependency IDs stay on the spec; this runtime link only drives observation.
    /// </summary>
    internal void ObserveDependency(ExecutionTask dependency)
    {
        if (dependency == null)
        {
            throw new ArgumentNullException(nameof(dependency));
        }

        if (dependency.Id == Id)
        {
            throw new InvalidOperationException($"Task '{Id}' cannot observe itself as a dependency.");
        }

        if (_observedDependencies.Any(existing => existing.Id == dependency.Id))
        {
            return;
        }

        _observedDependencies.Add(dependency);
        dependency.StateChanged += HandleObservedTaskChanged;
    }

    /// <summary>
    /// Recomputes this task's derived graph state after one observed child or dependency changed. Visible lifecycle and
    /// semantic outcome still come from this task's own projection logic, and subtree-rollup-only changes still publish
    /// through the same task event so observers can simply re-read current task properties.
    /// </summary>
    internal void RefreshDerivedStateFromObservations()
    {
        bool subtreeSchedulingChanged = RecomputeSubtreeSchedulingRollup();
        (ExecutionTaskState state, ExecutionTaskOutcome? outcome) = ComputeRolledUpStateFromChildren();
        if (!TransitionStatus(state, outcome) && subtreeSchedulingChanged)
        {
            RaiseStateChanged();
        }
    }

    /// <summary>
    /// Publishes this task's current task-owned state to observers without changing its visible runtime status.
    /// Structural graph mutations such as child insertion use this after wiring and cache rebuilds so observers can
    /// refresh from the new topology.
    /// </summary>
    internal void PublishStateChanged()
    {
        RaiseStateChanged();
    }

    /// <summary>
    /// Handles one observed child or dependency change by refreshing this task's own derived graph state.
    /// </summary>
    private void HandleObservedTaskChanged(ExecutionTask observedTask, ExecutionTaskState _, ExecutionTaskOutcome? __)
    {
        if (observedTask == null)
        {
            throw new ArgumentNullException(nameof(observedTask));
        }

        RefreshDerivedStateFromObservations();
    }

    internal void InitializeRuntimeState()
    {
        /* Live session tasks always begin in queued runtime state once the session clones and attaches them. Semantic
           outcome stays separate so disabled tasks can still render as Disabled while the scheduler treats them as
           already finished. */
        ExecutionTaskState initialState = Enabled ? ExecutionTaskState.Queued : ExecutionTaskState.Completed;
        State = initialState;
        Outcome = Enabled ? null : ExecutionTaskOutcome.Disabled;
        StartedAt = null;
        FinishedAt = null;
        ResetSubtreeMetrics();
        ResetSubtreeSchedulingRollup();
        lock (_activeExecutionSyncRoot)
        {
            _activeExecutionTask = null;
        }
    }

    /// <summary>
    /// Attaches one live execution handle to this task. Tasks own their active execution handles directly so the
    /// scheduler can scan the graph for in-flight work instead of maintaining a duplicated running-task registry.
    /// </summary>
    internal void AttachActiveExecution(Task<OperationResult> executionTask)
    {
        if (executionTask == null)
        {
            throw new ArgumentNullException(nameof(executionTask));
        }

        lock (_activeExecutionSyncRoot)
        {
            if (_activeExecutionTask != null)
            {
                throw new InvalidOperationException($"Task '{Id}' already has an active execution handle.");
            }

            _activeExecutionTask = executionTask;
        }
    }

    /// <summary>
    /// Clears the active execution handle from this task after the scheduler has observed completion. The completed task
    /// must match the attached handle exactly so ownership cannot drift between overlapping executions.
    /// </summary>
    internal void ClearActiveExecution(Task<OperationResult> executionTask)
    {
        if (executionTask == null)
        {
            throw new ArgumentNullException(nameof(executionTask));
        }

        lock (_activeExecutionSyncRoot)
        {
            if (!ReferenceEquals(_activeExecutionTask, executionTask))
            {
                throw new InvalidOperationException($"Task '{Id}' cannot clear a non-owned execution handle.");
            }

            _activeExecutionTask = null;
        }
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
    /// Assigns execution locks directly to this authored task body so tests and other builder-authored tasks can model
    /// contention at the exact task that should acquire the lock.
    /// </summary>
    internal void SetExecutionLocks(IReadOnlyList<ExecutionLock> executionLocks)
    {
        _ = executionLocks ?? throw new ArgumentNullException(nameof(executionLocks));
        _spec = _spec with
        {
            DeclaredExecutionLocks = executionLocks
                .Where(executionLock => executionLock != null)
                .Distinct()
                .ToList()
        };
    }

    /// <summary>
    /// Attaches one executable body directly to this authored task during plan building.
    /// </summary>
    internal void SetExecuteAsync(Func<ExecutionTaskContext, Task<OperationResult>> executeAsync)
    {
        _spec = _spec with { ExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync)) };
    }

    /// <summary>
    /// Builds the validated parameter view that both operation-declared locks and task execution use for this specific
    /// task identity.
    /// </summary>
    private ValidatedOperationParameters CreateValidatedOperationParameters()
    {
        return new ValidatedOperationParameters(Title, OperationParameters, DeclaredOptionTypes);
    }

    /// <summary>
    /// Controls whether this authored task is hidden in the graph projection until debugging reveals internal nodes.
    /// </summary>
    internal void SetHiddenInGraph(bool isHiddenInGraph)
    {
        _spec = _spec with { IsHiddenInGraph = isHiddenInGraph };
    }

    /// <summary>
    /// Stores optional task-local extension data for operation/runtime helpers. Core per-task runtime data such as
    /// lifecycle, subtree metrics, and logs live on dedicated task fields instead of in this extensibility bag.
    /// </summary>
    internal void SetData<T>(T value) where T : class
    {
        _dataByType[typeof(T)] = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Reads optional task-local extension data previously attached through <see cref="SetData{T}(T)"/>.
    /// </summary>
    internal bool TryGetLocalData<T>(out T? value) where T : class
    {
        if (_dataByType.TryGetValue(typeof(T), out object? rawValue) && rawValue is T typedValue)
        {
            value = typedValue;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Applies one complete runtime status update and raises property notifications only after the full state is
    /// internally consistent. This prevents observers from seeing torn combinations such as Running plus Result=Completed.
    /// </summary>
    internal void ApplyRuntimeState(ExecutionTaskState state, ExecutionTaskOutcome? outcome, DateTimeOffset? startedAt, DateTimeOffset? finishedAt)
    {
        bool stateChanged = !Equals(_state, state);
        bool outcomeChanged = !Equals(_outcome, outcome);
        bool startedAtChanged = !Equals(_startedAt, startedAt);
        bool finishedAtChanged = !Equals(_finishedAt, finishedAt);

        _state = state;
        _outcome = outcome;
        _startedAt = startedAt;
        _finishedAt = finishedAt;

        /* Task-local observers should see a self-consistent task status, including the task-owned subtree start-state
           rollup that feeds parent rollup decisions, before the task-level state-changed event fires. */
        bool subtreeSchedulingChanged = RecomputeSubtreeSchedulingRollup();

        if (stateChanged)
        {
            RaisePropertyChanged(nameof(State));
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

        if (stateChanged || outcomeChanged || subtreeSchedulingChanged)
        {
            RaiseStateChanged();
        }
    }

    /// <summary>
    /// Raises the task-owned change event while preserving the existing subscribe/inspect synchronization used by local
    /// waiters. The payload snapshots the current visible task state at raise time so subscribers can use it directly.
    /// </summary>
    private void RaiseStateChanged()
    {
        Action<ExecutionTask, ExecutionTaskState, ExecutionTaskOutcome?>? stateChanged;
        lock (_stateChangeSyncRoot)
        {
            stateChanged = StateChanged;
        }

        stateChanged?.Invoke(this, State, Outcome);
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
        return GetOwnWorkStartStateIgnoringAncestors() == TaskStartState.Ready && AreAncestorsOpen();
    }

    /// <summary>
    /// Returns the current local own-work scheduler state for this task body while intentionally ignoring ancestor
    /// gating above this task.
    /// </summary>
    private TaskStartState GetOwnWorkStartStateIgnoringAncestors()
    {
        if (_spec.ExecuteAsync == null || State == ExecutionTaskState.Completed)
        {
            return TaskStartState.NoStartableWork;
        }

        if (State == ExecutionTaskState.AwaitingLock && StartedAt == null)
        {
            return TaskStartState.AwaitingLock;
        }

        if (State == ExecutionTaskState.Queued && AreDependenciesSatisfied())
        {
            return TaskStartState.Ready;
        }

        if (State == ExecutionTaskState.Running && StartedAt != null && FinishedAt == null)
        {
            return TaskStartState.Running;
        }

        return State == ExecutionTaskState.Queued && !AreDependenciesSatisfied()
            ? TaskStartState.AwaitingDependency
            : TaskStartState.NoStartableWork;
    }

    /// <summary>
    /// Returns whether this task's own executable body is the task currently waiting on an execution lock. Direct task
    /// lock wait happens before the task has ever reached Running, so StartedAt remains unset. Rolled-up descendant lock
    /// wait can also use the AwaitingLock state, but those parents already have StartedAt once their own body
    /// began executing.
    /// </summary>
    internal bool IsOwnWorkWaitingForExecutionLock()
    {
        return GetOwnWorkStartStateIgnoringAncestors() == TaskStartState.AwaitingLock;
    }

    /// <summary>
    /// Returns whether descendant work beneath this task may start once ancestor gating above this task is ignored.
    /// Descendants are blocked when this scope itself is completed, doomed, or still waiting on its own dependencies.
    /// </summary>
    private bool IsScopeOpenForDescendantWork()
    {
        return State != ExecutionTaskState.Completed
            && Outcome == null
            && AreDependenciesSatisfied()
            && AreAncestorsOpen();
    }

    /// <summary>
    /// Combines two ancestor-independent subtree start states using the same priority order the scheduler relies on when
    /// deciding which scheduler frontier a subtree currently presents.
    /// </summary>
    private static TaskStartState CombineSubtreeStartStates(TaskStartState currentState, TaskStartState candidateState)
    {
        return GetSubtreeStartStatePriority(candidateState) > GetSubtreeStartStatePriority(currentState)
            ? candidateState
            : currentState;
    }

    /// <summary>
    /// Returns the fixed priority for one cached ancestor-independent subtree start state.
    /// </summary>
    private static int GetSubtreeStartStatePriority(TaskStartState state)
    {
        return state switch
        {
            TaskStartState.AwaitingLock => 4,
            TaskStartState.Ready => 3,
            TaskStartState.Running => 2,
            TaskStartState.AwaitingDependency => 1,
            _ => 0
        };
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
    /// Computes the rolled-up lifecycle state and semantic outcome for this task from its current local task facts plus
    /// the already-updated rolled-up state and cached scheduler summaries on its direct children. This is the single
    /// state-projection path for both direct task refresh and ancestor-container rollup.
    /// </summary>
    internal (ExecutionTaskState state, ExecutionTaskOutcome? outcome) ComputeRolledUpStateFromChildren()
    {
        bool ownTaskIsWaitingForExecutionLock = IsOwnWorkWaitingForExecutionLock();
        TaskStartState subtreeStartState = GetTaskStartState();
        /* Fold the direct-child contribution into one contiguous block here so parent rollup logic can read all child
           facts in one place without needing a separate helper type that merely transports these locals elsewhere. */
        bool childHasStartedWork = false;
        bool hasReadyChild = false;
        bool hasRunningChild = false;
        bool hasWaitingForExecutionLockChild = false;
        bool hasExternalDependencyWaitChild = false;
        bool hasQueuedChild = false;
        bool hasNonTerminalChild = false;
        ExecutionTask? failedChildTask = null;
        ExecutionTask? cancelledChildTask = null;
        ExecutionTask? interruptedChildTask = null;
        ExecutionTask? skippedChildTask = null;
        bool allChildrenDisabled = _children.Count > 0;
        foreach (ExecutionTask childTask in _children)
        {
            /* Each child's observed state and outcome already roll up its whole subtree, so the parent can infer whether
               work started anywhere below that child without carrying a second cached subtree-start flag. */
            childHasStartedWork |= childTask.HasStarted;
            hasReadyChild |= childTask._subtreeStartState == TaskStartState.Ready;
            hasRunningChild |= childTask._subtreeStartState == TaskStartState.Running;
            hasWaitingForExecutionLockChild |= childTask._subtreeStartState == TaskStartState.AwaitingLock;
            hasExternalDependencyWaitChild |= childTask._subtreeStartState == TaskStartState.AwaitingDependency
                && childTask.HasExternalDependencyWaitInSubtree(this);
            hasQueuedChild |= childTask.State is ExecutionTaskState.Queued or ExecutionTaskState.Planned;
            hasNonTerminalChild |= childTask.State != ExecutionTaskState.Completed;
            failedChildTask ??= childTask.Outcome == ExecutionTaskOutcome.Failed ? childTask : null;
            cancelledChildTask ??= childTask.Outcome == ExecutionTaskOutcome.Cancelled ? childTask : null;
            interruptedChildTask ??= childTask.Outcome == ExecutionTaskOutcome.Interrupted ? childTask : null;
            skippedChildTask ??= childTask.Outcome == ExecutionTaskOutcome.Skipped ? childTask : null;
            allChildrenDisabled &= childTask.Outcome == ExecutionTaskOutcome.Disabled;
        }

        bool subtreeHasStarted = HasStarted || childHasStartedWork;
        /* The visible scheduler frontier for this subtree comes from the cached subtree rollup, not only from the task's
           own local body. That preserves parent lock-wait and dependency-wait rollups when descendant work dominates the
           next reachable frontier while the task itself is still running or queued. */
        bool ownTaskIsRunning = State == ExecutionTaskState.Running && subtreeStartState == TaskStartState.Running;
        bool ownTaskIsQueued = State == ExecutionTaskState.Queued && subtreeStartState is TaskStartState.Ready or TaskStartState.AwaitingDependency;
        bool subtreeIsPureExecutionLockWait = subtreeHasStarted
            && !ownTaskIsRunning
            && hasWaitingForExecutionLockChild
            && !hasRunningChild
            && !hasExternalDependencyWaitChild
            && !hasReadyChild;
        bool subtreeIsPureDependencyWait = subtreeHasStarted
            && !ownTaskIsRunning
            && !ownTaskIsWaitingForExecutionLock
            && hasExternalDependencyWaitChild
            && !hasRunningChild
            && !hasWaitingForExecutionLockChild
            && !hasReadyChild;

        ExecutionTaskState parentState;
        if (Outcome is ExecutionTaskOutcome.Failed or ExecutionTaskOutcome.Cancelled or ExecutionTaskOutcome.Interrupted or ExecutionTaskOutcome.Skipped)
        {
            parentState = ExecutionTaskState.Completed;
        }
        else if (ownTaskIsWaitingForExecutionLock)
        {
            parentState = ExecutionTaskState.AwaitingLock;
        }
        else if (subtreeIsPureExecutionLockWait)
        {
            parentState = ExecutionTaskState.AwaitingLock;
        }
        else if (subtreeIsPureDependencyWait)
        {
            parentState = ExecutionTaskState.AwaitingDependency;
        }
        else if (ownTaskIsRunning || hasRunningChild || hasWaitingForExecutionLockChild)
        {
            parentState = ExecutionTaskState.Running;
        }
        else if (ownTaskIsQueued || hasQueuedChild || hasExternalDependencyWaitChild || hasNonTerminalChild)
        {
            parentState = subtreeHasStarted || Outcome != null
                ? ExecutionTaskState.Running
                : ExecutionTaskState.Queued;
        }
        else
        {
            parentState = ExecutionTaskState.Completed;
        }

        ExecutionTaskOutcome? parentOutcome = Outcome is ExecutionTaskOutcome.Failed or ExecutionTaskOutcome.Cancelled or ExecutionTaskOutcome.Interrupted or ExecutionTaskOutcome.Skipped
            ? Outcome
            : ownTaskIsRunning || ownTaskIsWaitingForExecutionLock || ownTaskIsQueued || subtreeIsPureDependencyWait || hasQueuedChild || hasRunningChild || hasWaitingForExecutionLockChild || hasExternalDependencyWaitChild || hasNonTerminalChild
                ? null
                : failedChildTask != null
                    ? ExecutionTaskOutcome.Failed
                    : cancelledChildTask != null
                        ? ExecutionTaskOutcome.Cancelled
                        : interruptedChildTask != null
                            ? ExecutionTaskOutcome.Interrupted
                            : allChildrenDisabled
                                ? ExecutionTaskOutcome.Disabled
                                : skippedChildTask != null
                                    ? ExecutionTaskOutcome.Skipped
                                    : ExecutionTaskOutcome.Completed;
        if (parentOutcome == ExecutionTaskOutcome.Completed && hasNonTerminalChild)
        {
            throw new InvalidOperationException($"Task '{Id}' cannot roll up to completed while child work is still non-terminal.");
        }

        return (parentState, parentOutcome);
    }

    /// <summary>
    /// Returns whether this subtree currently contributes a dependency-wait frontier whose blocking dependency lives
    /// outside the supplied ancestor scope. Queued descendants blocked only by unfinished work inside that scope are
    /// downstream of the frontier and therefore should not change the ancestor's rolled-up blocker state on their own.
    /// </summary>
    internal bool HasExternalDependencyWaitInSubtree(ExecutionTask scopeRoot)
    {
        if (scopeRoot == null)
        {
            throw new ArgumentNullException(nameof(scopeRoot));
        }

        if (_subtreeStartState != TaskStartState.AwaitingDependency)
        {
            return false;
        }

        if (GetOwnWorkStartStateIgnoringAncestors() == TaskStartState.AwaitingDependency
            && HasUnsatisfiedDependenciesOutsideScope(scopeRoot))
        {
            return true;
        }

        foreach (ExecutionTask child in _children)
        {
            if (child.HasExternalDependencyWaitInSubtree(scopeRoot))
            {
                return true;
            }
        }

        return false;
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
    /// Returns whether this task has at least one currently unsatisfied dependency whose blocking task lies outside the
    /// supplied ancestor scope. Disabled dependencies delegate to their own dependencies so disabled-through chains only
    /// count as external blockers when the first unfinished real blocker lies outside that scope.
    /// </summary>
    private bool HasUnsatisfiedDependenciesOutsideScope(ExecutionTask scopeRoot)
    {
        ExecutionTask root = GetTreeRoot();
        return Dependencies.Any(dependencyId => HasUnsatisfiedDependencyOutsideScope(root, scopeRoot, dependencyId));
    }

    /// <summary>
    /// Follows one dependency chain until it reaches the first still-unsatisfied blocker and reports whether that real
    /// blocker lies outside the supplied ancestor scope.
    /// </summary>
    private static bool HasUnsatisfiedDependencyOutsideScope(ExecutionTask root, ExecutionTask scopeRoot, ExecutionTaskId dependencyId)
    {
        ExecutionTask dependencyTask = root.FindTask(dependencyId)
            ?? throw new InvalidOperationException($"Dependency task '{dependencyId}' not found in the execution tree.");

        if (dependencyTask.Outcome == ExecutionTaskOutcome.Completed)
        {
            return false;
        }

        if (dependencyTask.Outcome == ExecutionTaskOutcome.Disabled)
        {
            return dependencyTask.Dependencies.Any(nextDependencyId => HasUnsatisfiedDependencyOutsideScope(root, scopeRoot, nextDependencyId));
        }

        return scopeRoot.FindTask(dependencyId) == null;
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
    /// Returns whether this task is currently in one of the non-terminal in-progress runtime states.
    /// Enum ordering intentionally keeps every such state strictly between Queued and Completed so callers can classify
    /// in-progress work without learning each individual state name.
    /// </summary>
    public bool IsInProgress => State > ExecutionTaskState.Queued && State < ExecutionTaskState.Completed;

    /// <summary>
    /// Returns whether this task's observed runtime status shows that work has started somewhere in the represented
    /// task subtree. Non-terminal started work uses in-progress lifecycle states, while terminal started work is carried
    /// by completed, failed, cancelled, or interrupted outcomes.
    /// </summary>
    internal bool HasStarted =>
        IsInProgress
        || Outcome is ExecutionTaskOutcome.Completed or ExecutionTaskOutcome.Cancelled or ExecutionTaskOutcome.Interrupted or ExecutionTaskOutcome.Failed;

    /// <summary>
    /// Resolves the timing timestamps that correspond to a lifecycle state transition on this task. Only actual Running
    /// work captures a start time, while completion records only the finish boundary for work that has already started.
    /// Tasks that wait on execution locks or complete without ever running therefore keep a null StartedAt so UI timing
    /// stays at its untouched placeholder until real execution begins.
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
            finishedAt = timestamp;
        }

        return (startedAt, finishedAt);
    }

    /// <summary>
    /// Guards lifecycle transitions so execution state stays monotonic once a task or container subtree has started.
    /// </summary>
    internal void ValidateStateTransition(ExecutionTaskState nextState)
    {
        if (State == ExecutionTaskState.Completed && nextState != ExecutionTaskState.Completed)
        {
            throw new InvalidOperationException($"Task '{Id}' cannot transition from completed execution state back to '{nextState}'.");
        }

        if (IsInProgress && nextState <= ExecutionTaskState.Queued)
        {
            /* Once a task or any descendant in its subtree has started, queued states are no longer legal. Container
               scopes must remain in an active non-queued state until they complete or fail, even if later descendant
               work is only waiting on dependencies, parent readiness, or execution locks. */
            throw new InvalidOperationException($"Task '{Id}' cannot transition from running back to queued state '{nextState}'.");
        }
    }

    /// <summary>
    /// Returns whether this task or any direct child status reports that work has already started in the represented
    /// subtree. Child statuses already roll up their own descendants, so direct-child inspection is sufficient here.
    /// </summary>
    private bool HasStartedSubtree => HasStarted || _children.Any(child => child.HasStarted);

    /// <summary>
    /// Guards combined observable state so lifecycle and semantic outcome never describe contradictory execution states.
    /// </summary>
    internal void ValidateObservedState(ExecutionTaskState state, ExecutionTaskOutcome? outcome)
    {
        if (state > ExecutionTaskState.Queued
            && state < ExecutionTaskState.Completed
            && outcome is (ExecutionTaskOutcome.Completed or ExecutionTaskOutcome.Skipped or ExecutionTaskOutcome.Disabled))
        {
            throw new InvalidOperationException($"Task '{Id}' cannot remain in progress while reporting semantic outcome '{outcome}'.");
        }

        if (state <= ExecutionTaskState.Queued && outcome != null)
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

        if (outcome == ExecutionTaskOutcome.Skipped && HasStartedSubtree)
        {
            throw new InvalidOperationException($"Task '{Title}' ({Id}) cannot be marked skipped after execution has started in its subtree.");
        }

        if (outcome == ExecutionTaskOutcome.Interrupted && !HasStartedSubtree)
        {
            throw new InvalidOperationException($"Task '{Title}' ({Id}) cannot be marked interrupted before execution has started in its subtree.");
        }
    }

    /// <summary>
    /// Returns whether any descendant beneath this task is still pending or running.
    /// </summary>
    internal bool HasNonTerminalDescendants()
    {
        return _children.Any(child => !child.IsTerminal || child.HasNonTerminalDescendants());
    }

    /// <summary>
    /// Skips every unfinished task in this subtree without disturbing tasks that already reached terminal states. Each
    /// skipped task validates its terminal assignment, transitions to completed, propagates to its own descendants, and
    /// raises the task-owned state-changed event so notifications come from the authoritative event surface.
    /// </summary>
    internal void SkipUnfinishedSubtree(ExecutionTaskOutcome skippedOutcome = ExecutionTaskOutcome.Skipped)
    {
        if (State is ExecutionTaskState.Planned or ExecutionTaskState.Queued)
        {
            CompleteWithOutcome(skippedOutcome);
        }

        foreach (ExecutionTask child in _children)
        {
            child.SkipUnfinishedSubtree(skippedOutcome);
        }
    }

    /// <summary>
    /// Skips every unfinished descendant beneath this task by walking each direct child subtree.
    /// </summary>
    internal void SkipUnfinishedDescendants(ExecutionTaskOutcome skippedOutcome = ExecutionTaskOutcome.Skipped)
    {
        foreach (ExecutionTask child in _children)
        {
            child.SkipUnfinishedSubtree(skippedOutcome);
        }
    }

    /// <summary>
    /// Marks one started subtree as interrupted after sibling failure. Started nodes keep their current lifecycle so any
    /// active descendant work can finish unwinding, while untouched nodes become skipped because they never ran.
    /// </summary>
    internal void MarkSubtreeInterrupted()
    {
        if (State != ExecutionTaskState.Completed)
        {
            if (HasStarted)
            {
                /* Started tasks receive an interrupted outcome without forcing lifecycle completion so any active
                   descendant work can finish unwinding naturally. */
                TransitionOutcome(ExecutionTaskOutcome.Interrupted);
            }
            else
            {
                CompleteWithOutcome(ExecutionTaskOutcome.Skipped);
            }
        }

        foreach (ExecutionTask child in _children)
        {
            child.MarkSubtreeInterrupted();
        }
    }

    /// <summary>
    /// Interrupts one sibling subtree after a different sibling has failed. Subtrees that already started work surface as
    /// Interrupted, while untouched subtrees remain Skipped.
    /// </summary>
    internal void InterruptSiblingSubtree(ExecutionTaskId failedSiblingRootId)
    {
        if (HasStartedSubtree)
        {
            MarkSubtreeInterrupted();
            return;
        }

        SkipUnfinishedSubtree(ExecutionTaskOutcome.Skipped);
    }

    /// <summary>
    /// Propagates a terminal outcome to untouched descendants so tasks that never started inherit a completed lifecycle
    /// plus the semantic outcome that explains why they will never run.
    /// </summary>
    internal void PropagateTerminalOutcomeToUntouchedDescendants(ExecutionTaskOutcome outcome)
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

        SkipUnfinishedDescendants(descendantOutcome);
    }

    /// <summary>
    /// Completes this task's lifecycle and assigns its semantic outcome, then propagates to untouched descendants.
    /// Ancestor refresh is handled by the session-level caller since it requires cross-branch coordination.
    /// </summary>
    internal void CompleteWithOutcome(ExecutionTaskOutcome outcome)
    {
        ValidateTerminalAssignment(outcome);
        TransitionStatus(ExecutionTaskState.Completed, outcome);
        PropagateTerminalOutcomeToUntouchedDescendants(outcome);
    }

    /// <summary>
    /// Completes this task's lifecycle while preserving any previously assigned doomed outcome (failed, cancelled, or
    /// interrupted) that was recorded while descendant work was still unwinding. When no doomed outcome exists, the
    /// provided success outcome is used instead.
    /// </summary>
    internal void CompleteLifecycle(ExecutionTaskOutcome successOutcome = ExecutionTaskOutcome.Completed)
    {
        ExecutionTaskOutcome finalOutcome = Outcome is ExecutionTaskOutcome.Failed or ExecutionTaskOutcome.Cancelled or ExecutionTaskOutcome.Interrupted
            ? Outcome.Value
            : successOutcome;
        CompleteWithOutcome(finalOutcome);
    }

    /// <summary>
    /// Marks this task as semantically interrupted without forcing lifecycle completion, so any active descendant work
    /// can finish unwinding naturally. Ancestor refresh is handled by the session-level caller.
    /// </summary>
    internal void Interrupt()
    {
        if (State == ExecutionTaskState.Completed)
        {
            throw new InvalidOperationException($"Task '{Id}' cannot be interrupted after it already completed.");
        }

        if (!HasStarted)
        {
            throw new InvalidOperationException($"Task '{Id}' cannot be interrupted before execution has started.");
        }

        TransitionOutcome(ExecutionTaskOutcome.Interrupted);
    }

    /// <summary>
    /// Validates and applies a lifecycle state transition on this task. Returns false when the transition is a no-op
    /// because the task already has the requested state.
    /// </summary>
    internal bool TransitionState(ExecutionTaskState state)
    {
        if (State == state)
        {
            return false;
        }

        ValidateStateTransition(state);
        ValidateObservedState(state, Outcome);
        (DateTimeOffset? startedAt, DateTimeOffset? finishedAt) = ResolveTaskTiming(state);
        ApplyRuntimeState(state, Outcome, startedAt, finishedAt);
        return true;
    }

    /// <summary>
    /// Validates and applies a semantic outcome transition on this task. Returns false when the transition is a no-op.
    /// </summary>
    internal bool TransitionOutcome(ExecutionTaskOutcome? outcome)
    {
        if (Outcome == outcome)
        {
            return false;
        }

        ValidateOutcomeTransition(outcome);
        ValidateObservedState(State, outcome);
        ApplyRuntimeState(State, outcome, StartedAt, FinishedAt);
        return true;
    }

    /// <summary>
    /// Validates and applies a combined lifecycle state and semantic outcome transition as a single atomic status update so
    /// observers never see torn state between lifecycle and outcome fields. Returns false when the transition is a no-op.
    /// </summary>
    internal bool TransitionStatus(ExecutionTaskState state, ExecutionTaskOutcome? outcome)
    {
        if (State == state && Outcome == outcome)
        {
            return false;
        }

        ValidateStateTransition(state);
        ValidateOutcomeTransition(outcome);
        ValidateObservedState(state, outcome);
        (DateTimeOffset? startedAt, DateTimeOffset? finishedAt) = ResolveTaskTiming(state);
        ApplyRuntimeState(state, outcome, startedAt, finishedAt);
        return true;
    }

}
