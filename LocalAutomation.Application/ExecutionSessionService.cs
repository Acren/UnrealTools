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
/// Starts, tracks, and manages the shared execution sessions surfaced to UI hosts.
/// </summary>
public sealed class ExecutionSessionService
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
    /// Builds and starts one execution session for the provided operation while letting callers attach UI listeners
    /// before execution begins.
    /// </summary>
    public LocalAutomation.Runtime.ExecutionSession StartExecution(Operation operation, OperationParameters parameters, Action<LocalAutomation.Runtime.ExecutionSession>? onSessionCreated = null)
    {
        if (operation == null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        BufferedLogStream logStream = new();
        LocalAutomation.Runtime.ExecutionPlan plan = ExecutionPlanFactory.BuildPlan(operation, parameters)
            ?? throw new InvalidOperationException($"Operation '{operation.OperationName}' did not produce an execution plan.");
        LocalAutomation.Runtime.ExecutionSession session = new(logStream, plan);

        /* Let UI consumers subscribe to task-status and task-log streams before execution begins so the first Running
           transition for long-lived tasks is visible on the graph instead of being lost during startup. */
        onSessionCreated?.Invoke(session);
        _sessions.Add(session);
        SessionsChanged?.Invoke();
        _ = RunAsync(session);
        return session;
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

    /// <summary>
    /// Runs one started session in the background so callers can receive the live session object immediately.
    /// </summary>
    private static async Task RunAsync(LocalAutomation.Runtime.ExecutionSession session)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionSessionService.RunAsync")
            .SetTag("operation.name", session.OperationName)
            .SetTag("target.name", session.TargetName)
            .SetTag("session.id", session.Id.Value)
            .SetTag("plan.task.count", session.Tasks.Count);
        try
        {
            await session.RunAsync();
            activity.SetTag("runtime.result", session.Outcome?.ToString() ?? string.Empty)
                .SetTag("session.outcome", session.Outcome.ToString());
        }
        catch (Exception ex)
        {
            activity.SetTag("runtime.result", "Exception")
                .SetTag("session.outcome", session.Outcome.ToString())
                .SetTag("exception.type", ex.GetType().FullName ?? ex.GetType().Name);
        }
        finally
        {
            activity.SetTag("session.is_running", session.IsRunning);
        }
    }
}
