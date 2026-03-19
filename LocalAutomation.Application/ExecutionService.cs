using System;
using System.Threading.Tasks;
using LocalAutomation.Core;

namespace LocalAutomation.Application;

/// <summary>
/// Holds the shared execution session state surfaced to UI hosts.
/// </summary>
public sealed class ExecutionService
{
    /// <summary>
    /// Raised when the current execution session changes.
    /// </summary>
    public event Action? SessionChanged;

    /// <summary>
    /// Gets the current execution session when one exists.
    /// </summary>
    public ExecutionSession? CurrentSession { get; private set; }

    /// <summary>
    /// Attaches a new execution session as the current one.
    /// </summary>
    public void SetCurrentSession(ExecutionSession session)
    {
        CurrentSession = session;
        SessionChanged?.Invoke();
    }

    /// <summary>
    /// Clears the current execution session.
    /// </summary>
    public void ClearCurrentSession()
    {
        CurrentSession = null;
        SessionChanged?.Invoke();
    }

    /// <summary>
    /// Cancels the current execution when one is active.
    /// </summary>
    public Task CancelAsync()
    {
        return CurrentSession != null ? CurrentSession.CancelAsync() : Task.CompletedTask;
    }
}
