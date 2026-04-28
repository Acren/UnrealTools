using System;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Runtime;

internal sealed class ExecutionTaskRuntimeServices
{
    private readonly ExecutionSession _session;
    private readonly ExecutionPlanScheduler _scheduler;
    private readonly Func<ExecutionTaskId, ILogger> _createLogger;

    public ExecutionTaskRuntimeServices(ExecutionSession session, ExecutionPlanScheduler scheduler, Func<ExecutionTaskId, ILogger> createLogger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _createLogger = createLogger ?? throw new ArgumentNullException(nameof(createLogger));
    }

    internal ExecutionTask GetTask(ExecutionTaskId taskId)
    {
        return _session.GetTask(taskId);
    }

    internal ILogger CreateLogger(ExecutionTaskId taskId)
    {
        return _createLogger(taskId);
    }

    internal void SetTaskState(ExecutionTaskId taskId, ExecutionTaskState state)
    {
        _session.SetTaskState(taskId, state);
    }

    internal ExecutionSessionId SessionId => _session.Id;

    /// <summary>
    /// Gets the absolute temp root reserved for the live session backing this task context.
    /// </summary>
    internal string SessionTempRootPath => _session.TempRootPath;

    internal Task<OperationResult> RunChildOperationAsync(Operation operation, OperationParameters operationParameters, ExecutionTaskContext parentContext, bool hideChildOperationRootInGraph)
    {
        return _session.RunChildOperationAsync(operation, operationParameters, parentContext, _scheduler, hideChildOperationRootInGraph);
    }
}

/// <summary>
/// Exposes the minimal runtime data available while resolving operation parameters before full task execution begins.
/// </summary>
public interface IOperationParameterContext
{
    /// <summary>
    /// Gets the task id for the active runtime context.
    /// </summary>
    ExecutionTaskId TaskId { get; }

    /// <summary>
    /// Gets the display title for the active runtime context.
    /// </summary>
    string Title { get; }

    /// <summary>
    /// Gets the logger scoped to the active runtime context.
    /// </summary>
    ILogger Logger { get; }

    /// <summary>
    /// Gets the cancellation token for the active runtime context.
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// Gets the operation associated with the active runtime context.
    /// </summary>
    Operation Operation { get; }

    /// <summary>
    /// Reads previously stored data from the current task or an ancestor task.
    /// </summary>
    T GetData<T>() where T : class;

    /// <summary>
    /// Tries to read previously stored data from the current task or an ancestor task.
    /// </summary>
    bool TryGetData<T>(out T? value) where T : class;
}

/// <summary>
/// Provides the minimal live-session surface needed to resolve operation parameters before a full task execution context
/// exists. Parameter resolution deliberately cannot read validated options because resolving the parameter bag is what
/// creates that validated view.
/// </summary>
internal sealed class ExecutionParameterContext : IOperationParameterContext
{
    private readonly ExecutionTaskRuntimeServices? _runtime;

    /// <summary>
    /// Creates one parameter-resolution context for a task that may not yet have a validated operation context.
    /// </summary>
    internal ExecutionParameterContext(ExecutionTaskId taskId, string title, ILogger logger, CancellationToken cancellationToken, Operation operation, ExecutionTaskRuntimeServices? runtime)
    {
        TaskId = taskId;
        Title = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("Execution step title is required.", nameof(title))
            : title;
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        CancellationToken = cancellationToken;
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        _runtime = runtime;
    }

    /// <summary>
    /// Gets the internal runtime identifier for the task whose parameters are being resolved.
    /// </summary>
    public ExecutionTaskId TaskId { get; }

    /// <summary>
    /// Gets the display title for the task whose parameters are being resolved.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the task logger available during parameter resolution.
    /// </summary>
    public ILogger Logger { get; }

    /// <summary>
    /// Gets the cancellation token for the live scheduler pass that requested parameter resolution.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Gets the operation whose parameter bag is being resolved.
    /// </summary>
    public Operation Operation { get; }

    /// <summary>
    /// Reads previously stored data from the current task or any ancestor task.
    /// </summary>
    public T GetData<T>() where T : class
    {
        if (TryGetData(out T? value))
        {
            return value ?? throw new InvalidOperationException($"Execution data '{typeof(T).FullName}' was resolved as null for task '{Title}'.");
        }

        throw new InvalidOperationException($"No execution data of type '{typeof(T).FullName}' is available for task '{Title}' or its ancestors.");
    }

    /// <summary>
    /// Tries to read previously stored data from the current task or any ancestor task.
    /// </summary>
    public bool TryGetData<T>(out T? value) where T : class
    {
        ExecutionTask? currentTask = GetRequiredTask();
        while (currentTask != null)
        {
            if (currentTask.TryGetLocalData(out value))
            {
                return true;
            }

            currentTask = currentTask.Parent;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Returns the current live session temp root when parameter resolution is running inside a session.
    /// </summary>
    internal bool TryGetSessionTempRootPath(out string sessionTempRootPath)
    {
        if (_runtime == null)
        {
            sessionTempRootPath = string.Empty;
            return false;
        }

        sessionTempRootPath = _runtime.SessionTempRootPath;
        return true;
    }

    /// <summary>
    /// Returns the live task for this context and throws when data APIs are used outside a live session run.
    /// </summary>
    private ExecutionTask GetRequiredTask()
    {
        if (_runtime == null)
        {
            throw new InvalidOperationException($"Execution data is only available while task '{Title}' is running inside a live session.");
        }

        return _runtime.GetTask(TaskId);
    }
}

/// <summary>
/// Carries the runtime services a declared plan step needs while it executes.
/// </summary>
public sealed class ExecutionTaskContext : IOperationParameterContext
{
    private readonly ExecutionTaskRuntimeServices? _runtime;

    /// <summary>
    /// Creates one execution context for a scheduled task invocation.
    /// </summary>
    internal ExecutionTaskContext(ExecutionTaskId taskId, string title, ILogger logger, CancellationToken cancellationToken, ValidatedOperationParameters validatedOperationParameters, Operation operation, ExecutionTaskRuntimeServices? runtime = null)
    {
        TaskId = taskId;
        Title = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("Execution step title is required.", nameof(title))
            : title;
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        CancellationToken = cancellationToken;
        ValidatedOperationParameters = validatedOperationParameters ?? throw new ArgumentNullException(nameof(validatedOperationParameters));
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        _runtime = runtime;
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
    /// Gets the operation-scoped validated parameter view so task bodies can read declared option sets without
    /// falling back to the raw parameter bag.
    /// </summary>
    public ValidatedOperationParameters ValidatedOperationParameters { get; }

    /// <summary>
    /// Gets the originating operation instance that authored the current task body.
    /// </summary>
    public Operation Operation { get; }

    /// <summary>
    /// Creates the live execution context for one task and applies any runtime parameter override declared by an imported
    /// child-operation root before the task body reads its validated parameter view.
    /// </summary>
    internal static ExecutionTaskContext CreateForRuntimeTask(ExecutionTask task, ILogger logger, CancellationToken cancellationToken, ExecutionTaskRuntimeServices runtime)
    {
        _ = task ?? throw new ArgumentNullException(nameof(task));
        _ = logger ?? throw new ArgumentNullException(nameof(logger));
        _ = runtime ?? throw new ArgumentNullException(nameof(runtime));

        ExecutionParameterContext parameterContext = new(task.Id, task.Title, logger, cancellationToken, task.Operation, runtime);
        OperationParameters operationParameters = task.ResolveOperationParameters(parameterContext);
        return CreateWithParameters(task, operationParameters, logger, cancellationToken, runtime);
    }

    /// <summary>
    /// Builds one context from a concrete parameter bag while keeping operation identity tied to the task that will run.
    /// </summary>
    private static ExecutionTaskContext CreateWithParameters(ExecutionTask task, OperationParameters operationParameters, ILogger logger, CancellationToken cancellationToken, ExecutionTaskRuntimeServices runtime)
    {
        ValidatedOperationParameters validatedOperationParameters = new(task.Title, operationParameters, task.DeclaredOptionTypes);
        return new ExecutionTaskContext(task.Id, task.Title, logger, cancellationToken, validatedOperationParameters, task.Operation, runtime);
    }

    /// <summary>
    /// Creates a sibling execution context for another task within the same logical execution flow while preserving the
    /// current cancellation token and live session.
    /// </summary>
    internal ExecutionTaskContext CreateForTask(ExecutionTask task)
    {
        ExecutionTaskRuntimeServices runtime = GetRequiredRuntime();
        return CreateForRuntimeTask(task, runtime.CreateLogger(task.Id), CancellationToken, runtime);
    }

    /// <summary>
    /// Runs a nested child operation through the same live runtime services without exposing session or scheduler objects
    /// directly to task code. Callers can hide the imported child root when the current task is only a thin wrapper around
    /// one child operation and showing both nodes would add duplicate graph noise.
    /// </summary>
    public Task<OperationResult> RunChildOperationAsync(Operation operation, OperationParameters operationParameters, bool hideChildOperationRootInGraph = false)
    {
        return GetRequiredRuntime().RunChildOperationAsync(operation, operationParameters, this, hideChildOperationRootInGraph);
    }

    /// <summary>
    /// Stores one strongly typed data value on the nearest operation root so sibling body tasks in the same nested
    /// operation can share state while logging and execution still stay attached to the currently running task.
    /// </summary>
    public void SetOperationData<T>(T value) where T : class
    {
        GetRequiredOperationRootTask().SetData(value);
    }

    /// <summary>
    /// Reads one previously stored data value from the nearest operation root.
    /// </summary>
    public T GetOperationData<T>() where T : class
    {
        if (TryGetOperationData(out T? value))
        {
            return value ?? throw new InvalidOperationException($"Operation data '{typeof(T).FullName}' was resolved as null for task '{Title}'.");
        }

        throw new InvalidOperationException($"No operation data of type '{typeof(T).FullName}' is available for task '{Title}'.");
    }

    /// <summary>
    /// Tries to read one previously stored data value from the nearest operation root.
    /// </summary>
    public bool TryGetOperationData<T>(out T? value) where T : class
    {
        return GetRequiredOperationRootTask().TryGetLocalData(out value);
    }

    /// <summary>
    /// Stores one strongly typed data value on the current task so descendant tasks can resolve it structurally through
    /// the ancestor chain.
    /// </summary>
    public void SetData<T>(T value) where T : class
    {
        GetRequiredTask().SetData(value);
    }

    /// <summary>
    /// Reads previously stored data from the current task or any ancestor task.
    /// </summary>
    public T GetData<T>() where T : class
    {
        if (TryGetData(out T? value))
        {
            return value ?? throw new InvalidOperationException($"Execution data '{typeof(T).FullName}' was resolved as null for task '{Title}'.");
        }

        throw new InvalidOperationException($"No execution data of type '{typeof(T).FullName}' is available for task '{Title}' or its ancestors.");
    }

    /// <summary>
    /// Tries to read previously stored data from the current task or any ancestor task.
    /// </summary>
    public bool TryGetData<T>(out T? value) where T : class
    {
        if (_runtime == null)
        {
            value = null;
            return false;
        }

        ExecutionTask? currentTask = GetRequiredTask();
        while (currentTask != null)
        {
            if (currentTask.TryGetLocalData(out value))
            {
                return true;
            }

            currentTask = currentTask.Parent;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Returns the live task for this context and throws when task-scoped state APIs are used outside a session run.
    /// </summary>
    private ExecutionTask GetRequiredTask()
    {
        if (_runtime == null)
        {
            throw new InvalidOperationException($"Execution data is only available while task '{Title}' is running inside a live session.");
        }

        return _runtime.GetTask(TaskId);
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

            currentTask = currentTask.Parent;
        }

        throw new InvalidOperationException($"Task '{Title}' is not contained within any operation root.");
    }

    /// <summary>
    /// Returns the current live session identifier when the task is executing inside a runtime session.
    /// </summary>
    internal bool TryGetSessionId(out ExecutionSessionId sessionId)
    {
        if (_runtime == null)
        {
            sessionId = default;
            return false;
        }

        sessionId = _runtime.SessionId;
        return true;
    }

    /// <summary>
    /// Returns the current live session's reserved temp root when the task is executing inside a runtime session.
    /// </summary>
    internal bool TryGetSessionTempRootPath(out string sessionTempRootPath)
    {
        if (_runtime == null)
        {
            sessionTempRootPath = string.Empty;
            return false;
        }

        sessionTempRootPath = _runtime.SessionTempRootPath;
        return true;
    }

    /// <summary>
    /// Returns the private runtime services for the current execution context.
    /// </summary>
    internal ExecutionTaskRuntimeServices GetRequiredRuntime()
    {
        return _runtime ?? throw new InvalidOperationException($"Task '{Title}' is not running inside live runtime services.");
    }
}
