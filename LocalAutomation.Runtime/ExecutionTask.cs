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
        Func<ExecutionTaskContext, Task<OperationResult>>? executeAsync = null)
    {
        Id = id;
        Title = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("Execution task title is required.", nameof(title))
            : title;
        Description = description ?? string.Empty;
        _parentId = parentId;
        _dependsOn = new List<ExecutionTaskId>((dependsOn ?? Array.Empty<ExecutionTaskId>()).Distinct());
        DependsOn = new ReadOnlyCollection<ExecutionTaskId>(_dependsOn);
        _childTaskIds = new List<ExecutionTaskId>();
        ChildTaskIds = new ReadOnlyCollection<ExecutionTaskId>(_childTaskIds);
        Enabled = enabled;
        DisabledReason = enabled ? string.Empty : (disabledReason ?? string.Empty);
        OperationParameters = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters));
        ExecuteAsync = executeAsync;
        LogStream = new BufferedLogStream();
        _status = enabled ? ExecutionTaskStatus.Planned : ExecutionTaskStatus.Disabled;
        _statusReason = enabled ? string.Empty : DisabledReason;
    }

    private readonly List<ExecutionTaskId> _dependsOn;
    private readonly List<ExecutionTaskId> _childTaskIds;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets the stable task identifier used by authored plans, live sessions, logs, and graph views.
    /// </summary>
    public ExecutionTaskId Id { get; }

    /// <summary>
    /// Gets the short title rendered on the graph canvas.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the longer descriptive text shown in details panels when one exists.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets the current parent task identifier for this live graph node.
    /// </summary>
    public ExecutionTaskId? ParentId
    {
        get => _parentId;
        internal set => SetProperty(ref _parentId, value);
    }

    /// <summary>
    /// Gets the explicit dependency ids that must complete before this task may run.
    /// </summary>
    public IReadOnlyList<ExecutionTaskId> DependsOn { get; }

    /// <summary>
    /// Gets the direct child task ids currently nested under this task in the live graph.
    /// </summary>
    public IReadOnlyList<ExecutionTaskId> ChildTaskIds { get; }

    /// <summary>
    /// Gets whether the task is configured to participate in execution.
    /// </summary>
    public bool Enabled { get; }

    /// <summary>
    /// Gets the explanation for why this task is disabled.
    /// </summary>
    public string DisabledReason { get; }

    /// <summary>
    /// Gets the parameter state captured for this task so execution callbacks receive the same explicit runtime input.
    /// </summary>
    public OperationParameters OperationParameters { get; }

    /// <summary>
    /// Gets the optional runtime callback that executes this task when the scheduler reaches it.
    /// </summary>
    public Func<ExecutionTaskContext, Task<OperationResult>>? ExecuteAsync { get; }

    /// <summary>
    /// Gets the current runtime status for this task in a live execution session.
    /// </summary>
    public ExecutionTaskStatus Status
    {
        get => _status;
        internal set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// Gets the explanatory text associated with the current status when one exists.
    /// </summary>
    public string StatusReason
    {
        get => _statusReason;
        internal set => SetProperty(ref _statusReason, value ?? string.Empty);
    }

    /// <summary>
    /// Gets the timestamp when this task first started running.
    /// </summary>
    public DateTimeOffset? StartedAt
    {
        get => _startedAt;
        internal set => SetProperty(ref _startedAt, value);
    }

    /// <summary>
    /// Gets the timestamp when this task reached its current terminal state.
    /// </summary>
    public DateTimeOffset? FinishedAt
    {
        get => _finishedAt;
        internal set => SetProperty(ref _finishedAt, value);
    }

    /// <summary>
    /// Gets the task-scoped buffered log stream used by selected-task views.
    /// </summary>
    public BufferedLogStream LogStream { get; }

    /// <summary>
    /// Creates a live session-owned clone of this task so runtime state mutations do not modify preview plan objects.
    /// </summary>
    internal ExecutionTask CloneForSession()
    {
        return new ExecutionTask(Id, Title, Description, ParentId, _dependsOn, Enabled, DisabledReason, OperationParameters, ExecuteAsync);
    }

    /// <summary>
    /// Initializes the task runtime state when it first enters a session.
    /// </summary>
    internal void InitializeRuntimeState(bool hasBegunExecution)
    {
        ExecutionTaskStatus initialStatus = Enabled
            ? (hasBegunExecution ? ExecutionTaskStatus.Pending : ExecutionTaskStatus.Planned)
            : ExecutionTaskStatus.Disabled;
        Status = initialStatus;
        StatusReason = Enabled ? string.Empty : DisabledReason;
        StartedAt = null;
        FinishedAt = null;
    }

    /// <summary>
    /// Adds one direct child id to this task if it is not already present.
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

    /// <summary>
    /// Adds one dependency id to this task if it is not already present.
    /// </summary>
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
    /// Raises a property-changed notification for one task field.
    /// </summary>
    private void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Updates one mutable property and raises change notifications only when the value actually changed.
    /// </summary>
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
