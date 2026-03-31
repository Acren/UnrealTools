using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using LocalAutomation.Core;
using LocalAutomation.Runtime;
using RuntimeExecutionSessionId = LocalAutomation.Runtime.ExecutionSessionId;

namespace LocalAutomation.Application;

/// <summary>
/// Holds the shared execution session state surfaced to UI hosts.
/// </summary>
public sealed class ExecutionService
{
    private readonly List<LocalAutomation.Runtime.ExecutionSession> _sessions = new();

    /// <summary>
    /// Raised when the execution session collection changes.
    /// </summary>
    public event Action? SessionsChanged;

    /// <summary>
    /// Gets the current execution sessions.
    /// </summary>
    public IReadOnlyList<LocalAutomation.Runtime.ExecutionSession> Sessions => new ReadOnlyCollection<LocalAutomation.Runtime.ExecutionSession>(_sessions);

    /// <summary>
    /// Adds a new execution session to the shared collection.
    /// </summary>
    public void AddSession(LocalAutomation.Runtime.ExecutionSession session)
    {
        if (session == null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        _sessions.Add(session);
        SessionsChanged?.Invoke();
    }

    /// <summary>
    /// Removes the execution session with the provided identifier.
    /// </summary>
    public void RemoveSession(RuntimeExecutionSessionId sessionId)
    {
        LocalAutomation.Runtime.ExecutionSession? existingSession = _sessions.FirstOrDefault(session => session.Id == sessionId);
        if (existingSession == null)
        {
            return;
        }

        _sessions.Remove(existingSession);
        SessionsChanged?.Invoke();
    }

    /// <summary>
    /// Cancels the execution with the provided identifier when it is active.
    /// </summary>
    public Task CancelAsync(RuntimeExecutionSessionId sessionId)
    {
        LocalAutomation.Runtime.ExecutionSession? session = _sessions.FirstOrDefault(item => item.Id == sessionId);
        return session != null ? session.CancelAsync() : Task.CompletedTask;
    }
}
