using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using LocalAutomation.Core;

namespace LocalAutomation.Application;

/// <summary>
/// Holds the shared execution session state surfaced to UI hosts.
/// </summary>
public sealed class ExecutionService
{
    private readonly List<ExecutionSession> _sessions = new();

    /// <summary>
    /// Raised when the execution session collection changes.
    /// </summary>
    public event Action? SessionsChanged;

    /// <summary>
    /// Gets the current execution sessions.
    /// </summary>
    public IReadOnlyList<ExecutionSession> Sessions => new ReadOnlyCollection<ExecutionSession>(_sessions);

    /// <summary>
    /// Adds a new execution session to the shared collection.
    /// </summary>
    public void AddSession(ExecutionSession session)
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
    public void RemoveSession(string sessionId)
    {
        ExecutionSession? existingSession = _sessions.FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.Ordinal));
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
    public Task CancelAsync(string sessionId)
    {
        ExecutionSession? session = _sessions.FirstOrDefault(item => string.Equals(item.Id, sessionId, StringComparison.Ordinal));
        return session != null ? session.CancelAsync() : Task.CompletedTask;
    }
}
