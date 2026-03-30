using System;

namespace LocalAutomation.Core;

/// <summary>
/// Describes one task in an execution plan, including its identity, display metadata, and preview-state hints.
/// </summary>
public sealed class ExecutionTask
{
    /// <summary>
    /// Creates an execution-plan task with the provided metadata.
    /// </summary>
    public ExecutionTask(
        string id,
        string title,
        string? description = null,
        ExecutionTaskKind kind = ExecutionTaskKind.Task,
        string? parentId = null,
        ExecutionTaskStatus status = ExecutionTaskStatus.Planned,
        string? statusReason = null)
    {
        Id = string.IsNullOrWhiteSpace(id)
            ? throw new ArgumentException("Execution task id is required.", nameof(id))
            : id;
        Title = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("Execution task title is required.", nameof(title))
            : title;
        Description = description ?? string.Empty;
        Kind = kind;
        ParentId = string.IsNullOrWhiteSpace(parentId) ? null : parentId;
        Status = status;
        StatusReason = statusReason ?? string.Empty;
    }

    /// <summary>
    /// Gets the stable task identifier used by preview and session views.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the short title rendered on the graph canvas.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the longer descriptive text shown in details panels when one exists.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets the parent grouping task identifier when this task participates in a visual hierarchy.
    /// </summary>
    public string? ParentId { get; }

    /// <summary>
    /// Gets the task kind used for grouping and rendering semantics.
    /// </summary>
    public ExecutionTaskKind Kind { get; }

    /// <summary>
    /// Gets the current preview or runtime status for the task.
    /// </summary>
    public ExecutionTaskStatus Status { get; }

    /// <summary>
    /// Gets the explanatory status text shown when the task is disabled, blocked, skipped, or otherwise notable.
    /// </summary>
    public string StatusReason { get; }

    /// <summary>
    /// Gets whether the task is configured to participate in execution.
    /// </summary>
    public bool IsEnabled => Status != ExecutionTaskStatus.Disabled;
}
