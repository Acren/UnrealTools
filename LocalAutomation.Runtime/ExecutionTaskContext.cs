using System;
using System.Collections.Generic;
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
    public ExecutionTaskContext(ExecutionTaskId taskId, string title, ILogger logger, CancellationToken cancellationToken, OperationParameters operationParameters, IDictionary<Type, object> sharedData)
    {
        TaskId = taskId;
        Title = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("Execution step title is required.", nameof(title))
            : title;
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        CancellationToken = cancellationToken;
        OperationParameters = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters));
        SharedData = sharedData ?? throw new ArgumentNullException(nameof(sharedData));
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
    /// Gets the parameter state that authored the current execution plan so task callbacks do not have to read it from
    /// mutable operation instance state.
    /// </summary>
    public OperationParameters OperationParameters { get; }

    /// <summary>
    /// Gets the shared plan-scoped data bag used by cooperating tasks to exchange strongly typed execution state.
    /// </summary>
    private IDictionary<Type, object> SharedData { get; }

    /// <summary>
    /// Stores or replaces one plan-scoped value by its CLR type so later tasks can consume the state produced by an
    /// earlier task without routing it through mutable operation instance fields.
    /// </summary>
    public void SetSharedData<T>(T value) where T : class
    {
        SharedData[typeof(T)] = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Reads a previously stored plan-scoped value by type and throws when no task has produced it yet.
    /// </summary>
    public T GetRequiredSharedData<T>() where T : class
    {
        if (TryGetSharedData(out T? value))
        {
            return value ?? throw new InvalidOperationException($"Shared execution state '{typeof(T).FullName}' was resolved as null for task '{Title}'.");
        }

        throw new InvalidOperationException($"No shared execution state of type '{typeof(T).FullName}' is available for task '{Title}'.");
    }

    /// <summary>
    /// Tries to read one previously stored plan-scoped value by type.
    /// </summary>
    public bool TryGetSharedData<T>(out T? value) where T : class
    {
        if (SharedData.TryGetValue(typeof(T), out object? rawValue) && rawValue is T typedValue)
        {
            value = typedValue;
            return true;
        }

        value = null;
        return false;
    }
}
