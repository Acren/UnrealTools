using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

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

    /// <summary>
    /// Creates one execution task from authored metadata. Sessions later clone these tasks into live nodes and initialize
    /// their mutable runtime state independently.
    /// </summary>
    public ExecutionTask(
        ExecutionTaskId id,
        string title,
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
        bool isCallbackTask = false,
        ExecutionTaskId? callbackOwnerTaskId = null)
    {
        Id = id;
        Title = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("Execution task title is required.", nameof(title))
            : title;
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
        IsCallbackTask = isCallbackTask;
        CallbackOwnerTaskId = callbackOwnerTaskId;
        LogStream = new BufferedLogStream();

        /* Runtime lifecycle and semantic outcome are tracked separately. Disabled tasks start in a completed lifecycle
           state because no scheduler work remains for them, while their semantic outcome still reports Disabled. */
        _state = enabled ? ExecutionTaskState.Planned : ExecutionTaskState.Completed;
        _statusReason = enabled ? string.Empty : DisabledReason;
        _outcome = enabled ? outcome : ExecutionTaskOutcome.Disabled;
    }

    private readonly List<ExecutionTaskId> _dependsOn;
    private readonly List<ExecutionTaskId> _childTaskIds;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ExecutionTaskId Id { get; }

    public string Title { get; }

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
     /// Gets whether this task is an implicit visible callback node lowered from an authored `.Run(...)` declaration.
     /// </summary>
    public bool IsCallbackTask { get; }

    /// <summary>
    /// Gets the authored/container task that owns this callback task when one exists.
    /// </summary>
    public ExecutionTaskId? CallbackOwnerTaskId { get; }

    public BufferedLogStream LogStream { get; }

    internal ExecutionTask CloneForSession()
    {
        return new ExecutionTask(Id, Title, Description, ParentId, _dependsOn, Enabled, DisabledReason, OperationParameters, DeclaredOptionTypes, ExecuteAsync, Outcome, IsOperationRoot, IsCallbackTask, CallbackOwnerTaskId);
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
}
