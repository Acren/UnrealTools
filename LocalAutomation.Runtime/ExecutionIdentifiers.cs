using System;

namespace LocalAutomation.Runtime;

/// <summary>
/// Represents a stable execution-plan identifier used by runtime, UI, and persistence boundaries.
/// </summary>
public readonly record struct ExecutionPlanId
{
    public ExecutionPlanId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Execution plan id must be provided.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

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
    public ExecutionTaskId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Execution task id must be provided.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    public static ExecutionTaskId New()
    {
        return new ExecutionTaskId(Guid.NewGuid().ToString("N"));
    }

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
    public ExecutionSessionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Execution session id must be provided.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    public static ExecutionSessionId New()
    {
        return new ExecutionSessionId(Guid.NewGuid().ToString("N"));
    }

    public static ExecutionSessionId? FromNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : new ExecutionSessionId(value);
    }
}
