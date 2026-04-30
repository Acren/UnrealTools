using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using LocalAutomation.Core;
using LocalAutomation.Runtime;
using Microsoft.Extensions.Logging;
using RuntimeExecutionSessionId = LocalAutomation.Runtime.ExecutionSessionId;

namespace LocalAutomation.Application;

/// <summary>
/// Starts, tracks, and manages the shared execution sessions surfaced to UI hosts.
/// </summary>
public sealed class ExecutionSessionService
{
    // Tracks live and completed execution sessions in display order for application hosts.
    private readonly List<LocalAutomation.Runtime.ExecutionSession> _sessions = new();
    // Holds the optional directory where each execution session writes its own durable log file.
    private readonly string? _sessionLogDirectory;

    /// <summary>
    /// Creates the session service and optionally enables per-execution disk logging under the supplied directory.
    /// </summary>
    public ExecutionSessionService(string? sessionLogDirectory = null)
    {
        _sessionLogDirectory = string.IsNullOrWhiteSpace(sessionLogDirectory) ? null : sessionLogDirectory;
    }

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
        ExecutionSessionLogWriter? logWriter = ExecutionSessionLogWriter.TryAttach(session, _sessionLogDirectory);

        // Transfer ownership of the file writer to the background run only after all synchronous startup hooks succeed.
        bool runStarted = false;
        try
        {
            if (logWriter != null)
            {
                session.Logger.LogInformation("Writing session log to {SessionLogFilePath}", logWriter.FilePath);
            }

            /* Let UI consumers subscribe to task-status and task-log streams before execution begins so the first Running
               transition for long-lived tasks is visible on the graph instead of being lost during startup. */
            onSessionCreated?.Invoke(session);
            _sessions.Add(session);
            SessionsChanged?.Invoke();
            _ = RunAsync(session, logWriter);
            runStarted = true;
            return session;
        }
        finally
        {
            if (!runStarted)
            {
                logWriter?.Dispose();
            }
        }
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
    private static async Task RunAsync(LocalAutomation.Runtime.ExecutionSession session, ExecutionSessionLogWriter? logWriter)
    {
        using (logWriter)
        {
            try
            {
                await session.RunAsync();
            }
            catch (Exception ex)
            {
                /* Background execution failures belong to the session that produced them. Logging through the session
                   logger keeps the execution tab and the per-session disk file as the durable diagnostic surfaces. */
                session.Logger.LogError(ex, "Execution session '{SessionId}' failed for '{OperationName}'.", session.Id.Value, session.OperationName);
            }
        }
    }
}
