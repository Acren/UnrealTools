using System;
using System.Threading;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Runtime;

/// <summary>
/// Carries the runtime services a declared plan step needs while it executes.
/// </summary>
public sealed class ExecutionTaskContext
{
    /// <summary>
    /// Creates one execution context for a scheduled task invocation.
    /// </summary>
    public ExecutionTaskContext(ExecutionTaskId taskId, string title, ILogger logger, CancellationToken cancellationToken)
    {
        TaskId = taskId;
        Title = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("Execution step title is required.", nameof(title))
            : title;
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets the internal runtime identifier for the executing task.
    /// </summary>
    public ExecutionTaskId TaskId { get; }

    /// <summary>
    /// Gets the display title for the executing task.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the logger scoped to the current task.
    /// </summary>
    public ILogger Logger { get; }

    /// <summary>
    /// Gets the active cancellation token for the task execution.
    /// </summary>
    public CancellationToken CancellationToken { get; }
}
