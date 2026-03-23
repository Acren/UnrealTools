using System;

namespace LocalAutomation.Extensions.Abstractions;

/// <summary>
/// Represents a stable operation identifier used for in-memory business logic and persistence boundaries.
/// </summary>
public readonly record struct OperationId
{
    /// <summary>
    /// Creates a typed operation identifier from its serialized value.
    /// </summary>
    public OperationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Operation id must be provided.", nameof(value));
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
    public static OperationId? FromNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : new OperationId(value);
    }

    /// <summary>
    /// Implicitly converts the typed id back to its serialized string value.
    /// </summary>
    public static implicit operator string(OperationId value) => value.Value;
}

/// <summary>
/// Represents a stable target-type identifier used to classify runtime targets in live business logic.
/// </summary>
public readonly record struct TargetTypeId
{
    /// <summary>
    /// Creates a typed target-type identifier from its serialized value.
    /// </summary>
    public TargetTypeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Target type id must be provided.", nameof(value));
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
    public static TargetTypeId? FromNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : new TargetTypeId(value);
    }

    /// <summary>
    /// Implicitly converts the typed id back to its serialized string value.
    /// </summary>
    public static implicit operator string(TargetTypeId value) => value.Value;
}

/// <summary>
/// Represents a stable target context action identifier used for in-memory business logic and registry uniqueness.
/// </summary>
public readonly record struct ContextActionId
{
    /// <summary>
    /// Creates a typed context action identifier from its serialized value.
    /// </summary>
    public ContextActionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Context action id must be provided.", nameof(value));
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
    public static ContextActionId? FromNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : new ContextActionId(value);
    }

    /// <summary>
    /// Implicitly converts the typed id back to its serialized string value.
    /// </summary>
    public static implicit operator string(ContextActionId value) => value.Value;
}

/// <summary>
/// Represents a stable option field identifier used by metadata-driven editors.
/// </summary>
public readonly record struct OptionFieldId
{
    /// <summary>
    /// Creates a typed option field identifier from its serialized value.
    /// </summary>
    public OptionFieldId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Option field id must be provided.", nameof(value));
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
    /// Implicitly converts the typed id back to its serialized string value.
    /// </summary>
    public static implicit operator string(OptionFieldId value) => value.Value;
}
