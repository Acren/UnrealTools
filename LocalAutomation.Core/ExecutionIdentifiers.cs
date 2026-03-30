using System;

namespace LocalAutomation.Core;

/// <summary>
/// Represents a stable execution-plan identifier used by runtime, UI, and persistence boundaries.
/// </summary>
public readonly record struct ExecutionPlanId
{
    /// <summary>
    /// Creates a typed execution-plan identifier from its serialized value.
    /// </summary>
    public ExecutionPlanId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Execution plan id must be provided.", nameof(value));
        }

        Value = value;
    }

    /// <summary>
    /// Gets the serialized identifier value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Returns the serialized identifier value.
    /// </summary>
    public override string ToString() => Value;

    /// <summary>
    /// Creates a typed identifier when the provided value is non-empty; otherwise returns <c>null</c>.
    /// </summary>
    public static ExecutionPlanId? FromNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : new ExecutionPlanId(value);
    }
}

/// <summary>
/// Represents a stable execution-task identifier used across plans, schedulers, logs, and graph rendering.
/// </summary>
public readonly record struct ExecutionTaskId
{
    /// <summary>
    /// Creates a typed execution-task identifier from its serialized value.
    /// </summary>
    public ExecutionTaskId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Execution task id must be provided.", nameof(value));
        }

        Value = value;
    }

    /// <summary>
    /// Gets the serialized identifier value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Returns the serialized identifier value.
    /// </summary>
    public override string ToString() => Value;

    /// <summary>
    /// Creates a new unique execution-task identifier for synthetic nodes or generated tasks.
    /// </summary>
    public static ExecutionTaskId New()
    {
        return new ExecutionTaskId(Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Creates a typed identifier when the provided value is non-empty; otherwise returns <c>null</c>.
    /// </summary>
    public static ExecutionTaskId? FromNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : new ExecutionTaskId(value);
    }
}

/// <summary>
/// Represents a stable execution-session identifier used by hosts to track live and completed runs.
/// </summary>
public readonly record struct ExecutionSessionId
{
    /// <summary>
    /// Creates a typed execution-session identifier from its serialized value.
    /// </summary>
    public ExecutionSessionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Execution session id must be provided.", nameof(value));
        }

        Value = value;
    }

    /// <summary>
    /// Gets the serialized identifier value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Returns the serialized identifier value.
    /// </summary>
    public override string ToString() => Value;

    /// <summary>
    /// Creates a new unique execution-session identifier for a freshly started run.
    /// </summary>
    public static ExecutionSessionId New()
    {
        return new ExecutionSessionId(Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Creates a typed identifier when the provided value is non-empty; otherwise returns <c>null</c>.
    /// </summary>
    public static ExecutionSessionId? FromNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : new ExecutionSessionId(value);
    }
}
