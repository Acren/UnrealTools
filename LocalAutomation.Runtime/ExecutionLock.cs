using System;

namespace LocalAutomation.Runtime;

/// <summary>
/// Represents one named in-process execution lock. Operations consume predefined values or helper factories instead of
/// constructing ad hoc runtime lock keys inline.
/// </summary>
public sealed class ExecutionLock : IEquatable<ExecutionLock>
{
    public ExecutionLock(string family, string scope)
    {
        Family = string.IsNullOrWhiteSpace(family)
            ? throw new ArgumentException("Execution lock family is required.", nameof(family))
            : family;
        Scope = string.IsNullOrWhiteSpace(scope)
            ? throw new ArgumentException("Execution lock scope is required.", nameof(scope))
            : scope;
    }

    /// <summary>
    /// Gets the logical lock family name used to group related lock instances.
    /// </summary>
    public string Family { get; }

    /// <summary>
    /// Gets the logical lock scope within the family.
    /// </summary>
    public string Scope { get; }

    /// <summary>
    /// Gets the normalized runtime key used by the in-process semaphore table.
    /// </summary>
    public string Key => Family + ":" + Scope;

    public bool Equals(ExecutionLock? other)
    {
        return other != null &&
            string.Equals(Family, other.Family, StringComparison.Ordinal) &&
            string.Equals(Scope, other.Scope, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as ExecutionLock);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(Family),
            StringComparer.Ordinal.GetHashCode(Scope));
    }
}
