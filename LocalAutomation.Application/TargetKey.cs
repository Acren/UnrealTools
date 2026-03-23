using System;

namespace LocalAutomation.Application;

/// <summary>
/// Represents a stable persistence key used to correlate host-managed state with one concrete target instance.
/// </summary>
public readonly record struct TargetKey
{
    /// <summary>
    /// Creates a typed target key from its serialized value.
    /// </summary>
    public TargetKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Target key must be provided.", nameof(value));
        }

        Value = value;
    }

    /// <summary>
    /// Gets the serialized key value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Returns the serialized key value.
    /// </summary>
    public override string ToString() => Value;

    /// <summary>
    /// Creates a typed key when the provided value is non-empty; otherwise returns <c>null</c>.
    /// </summary>
    public static TargetKey? FromNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : new TargetKey(value);
    }

    /// <summary>
    /// Implicitly converts the typed key back to its serialized string value.
    /// </summary>
    public static implicit operator string(TargetKey value) => value.Value;
}
