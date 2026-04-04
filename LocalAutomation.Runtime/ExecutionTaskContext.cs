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
    public ExecutionTaskContext(ExecutionTaskId taskId, string title, ILogger logger, CancellationToken cancellationToken, ValidatedOperationParameters validatedOperationParameters, Operation operation, ExecutionSession? session = null, ExecutionPlanScheduler? scheduler = null)
    {
        TaskId = taskId;
        Title = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("Execution step title is required.", nameof(title))
            : title;
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        CancellationToken = cancellationToken;
        ValidatedOperationParameters = validatedOperationParameters ?? throw new ArgumentNullException(nameof(validatedOperationParameters));
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        Session = session;
        Scheduler = scheduler;
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

    /// <summary>
    /// Gets the operation-scoped validated parameter view so task callbacks can read declared option sets without
    /// falling back to the raw parameter bag.
    /// </summary>
    public ValidatedOperationParameters ValidatedOperationParameters { get; }

    /// <summary>
    /// Gets the originating operation instance that authored the current task callback.
    /// </summary>
    public Operation Operation { get; }

    /// <summary>
    /// Gets the live execution session when the current task is running inside a session-backed execution. Runtime child
    /// operation attachment flows through this session rather than through the immutable plan that originally seeded it.
    /// </summary>
    public ExecutionSession? Session { get; }

    /// <summary>
    /// Gets the current execution session identifier when the task is running inside a live session.
    /// </summary>
    public ExecutionSessionId? SessionId => Session?.Id;

    /// <summary>
    /// Gets the active root scheduler when this task is running inside a scheduler-owned execution session. Nested child
    /// operations re-enter this same scheduler so the run continues on one live session graph instead of creating a
    /// detached child execution path.
    /// </summary>
    internal ExecutionPlanScheduler? Scheduler { get; }

    /// <summary>
    /// Creates a sibling execution context for another task within the same logical execution flow while preserving the
    /// current cancellation token and live session.
     /// </summary>
    internal ExecutionTaskContext CreateForTask(ExecutionTaskId taskId, string title, ILogger logger, ValidatedOperationParameters validatedOperationParameters)
    {
        return new ExecutionTaskContext(taskId, title, logger, CancellationToken, validatedOperationParameters, Operation, Session, Scheduler);
    }

    /// <summary>
    /// Stores one strongly typed state value on the nearest operation root so sibling callbacks in the same nested
    /// operation can share state while logging and execution still stay attached to the currently running task.
    /// </summary>
    public void SetOperationState<T>(T value) where T : class
    {
        GetRequiredOperationRootTask().SetState(value);
    }

    /// <summary>
    /// Reads one previously stored state value from the nearest operation root.
    /// </summary>
    public T GetOperationState<T>() where T : class
    {
        if (TryGetOperationState(out T? value))
        {
            return value ?? throw new InvalidOperationException($"Operation state '{typeof(T).FullName}' was resolved as null for task '{Title}'.");
        }

        throw new InvalidOperationException($"No operation state of type '{typeof(T).FullName}' is available for task '{Title}'.");
    }

    /// <summary>
    /// Tries to read one previously stored state value from the nearest operation root.
    /// </summary>
    public bool TryGetOperationState<T>(out T? value) where T : class
    {
        return GetRequiredOperationRootTask().TryGetLocalState(out value);
    }

    /// <summary>
    /// Stores one strongly typed state value on the current task so descendant tasks can resolve it structurally through
    /// the ancestor chain.
     /// </summary>
    public void SetState<T>(T value) where T : class
    {
        GetRequiredTask().SetState(value);
    }

    /// <summary>
    /// Reads a previously stored state value from the current task or any ancestor task.
     /// </summary>
    public T GetState<T>() where T : class
    {
        if (TryGetState(out T? value))
        {
            return value ?? throw new InvalidOperationException($"Execution state '{typeof(T).FullName}' was resolved as null for task '{Title}'.");
        }

        throw new InvalidOperationException($"No execution state of type '{typeof(T).FullName}' is available for task '{Title}' or its ancestors.");
    }

    /// <summary>
    /// Tries to read one previously stored state value from the current task or any ancestor task.
     /// </summary>
    public bool TryGetState<T>(out T? value) where T : class
    {
        if (Session == null)
        {
            value = null;
            return false;
        }

        ExecutionTask? currentTask = GetRequiredTask();
        while (currentTask != null)
        {
            if (currentTask.TryGetLocalState(out value))
            {
                return true;
            }

            currentTask = currentTask.ParentId is ExecutionTaskId parentId
                ? Session.GetTask(parentId)
                : null;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Returns the live task for this context and throws when task-scoped state APIs are used outside a session run.
    /// </summary>
    private ExecutionTask GetRequiredTask()
    {
        if (Session == null)
        {
            throw new InvalidOperationException($"Execution state is only available while task '{Title}' is running inside a live session.");
        }

        return Session.GetTask(TaskId);
    }

    /// <summary>
    /// Returns the nearest ancestor task that represents the root of the current nested operation.
    /// </summary>
    private ExecutionTask GetRequiredOperationRootTask()
    {
        ExecutionTask? currentTask = GetRequiredTask();
        while (currentTask != null)
        {
            if (currentTask.IsOperationRoot)
            {
                return currentTask;
            }

            currentTask = currentTask.ParentId is ExecutionTaskId parentId
                ? Session!.GetTask(parentId)
                : null;
        }

        throw new InvalidOperationException($"Task '{Title}' is not contained within any operation root.");
    }
}
