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
    private ExecutionTaskStatus _status;
    private string _statusReason;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;
    private ExecutionTaskStatus? _result;

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
        ExecutionTaskStatus? result = null,
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
        Result = result;
        IsCallbackTask = isCallbackTask;
        CallbackOwnerTaskId = callbackOwnerTaskId;
        LogStream = new BufferedLogStream();

        /* Runtime lifecycle and semantic outcome are tracked separately. Disabled tasks start in a completed lifecycle
           state because no scheduler work remains for them, while their semantic outcome still reports Disabled. */
        _status = enabled ? ExecutionTaskStatus.Planned : ExecutionTaskStatus.Completed;
        _statusReason = enabled ? string.Empty : DisabledReason;
        _result = enabled ? result : ExecutionTaskStatus.Disabled;
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
    public ExecutionTaskStatus Status
    {
        get => _status;
        internal set => SetProperty(ref _status, value);
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
    public ExecutionTaskStatus? Result
    {
        get => _result;
        internal set => SetProperty(ref _result, value);
    }

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
        return new ExecutionTask(Id, Title, Description, ParentId, _dependsOn, Enabled, DisabledReason, OperationParameters, DeclaredOptionTypes, ExecuteAsync, Result, IsCallbackTask, CallbackOwnerTaskId);
    }

    internal void InitializeRuntimeState(bool hasBegunExecution)
    {
        /* Sessions reset only lifecycle state here. Semantic outcome stays separate so disabled tasks can still render as
           Disabled while the scheduler treats them as already finished. */
        ExecutionTaskStatus initialStatus = Enabled
            ? (hasBegunExecution ? ExecutionTaskStatus.Pending : ExecutionTaskStatus.Planned)
            : ExecutionTaskStatus.Completed;
        Status = initialStatus;
        StatusReason = Enabled ? string.Empty : DisabledReason;
        Result = Enabled ? null : ExecutionTaskStatus.Disabled;
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
