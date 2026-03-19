using System;
using System.Threading.Tasks;

namespace LocalAutomation.Core;

/// <summary>
/// Represents a running or recently completed execution owned by the shared automation layers.
/// </summary>
public sealed class ExecutionSession
{
    private readonly Func<Task>? _cancelAsync;

    /// <summary>
    /// Creates an execution session around a shared log stream and optional cancellation action.
    /// </summary>
    public ExecutionSession(ILogStream logStream, Func<Task>? cancelAsync = null)
    {
        LogStream = logStream ?? throw new ArgumentNullException(nameof(logStream));
        _cancelAsync = cancelAsync;
    }

    /// <summary>
    /// Gets the live log stream associated with this execution.
    /// </summary>
    public ILogStream LogStream { get; }

    /// <summary>
    /// Gets or sets whether the execution is currently running.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// Gets or sets whether the execution completed successfully.
    /// </summary>
    public bool? Success { get; set; }

    /// <summary>
    /// Gets or sets the operation display name for the current session.
    /// </summary>
    public string OperationName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target display name for the current session.
    /// </summary>
    public string TargetName { get; set; } = string.Empty;

    /// <summary>
    /// Cancels the underlying execution when cancellation is available.
    /// </summary>
    public Task CancelAsync()
    {
        return _cancelAsync != null ? _cancelAsync() : Task.CompletedTask;
    }
}
