using System;

namespace LocalAutomation.Runtime;

/// <summary>
/// Represents one named in-process execution lock. Operations consume predefined values or helper factories instead of
/// constructing ad hoc runtime lock keys inline.
/// </summary>
public sealed class ExecutionLock : IEquatable<ExecutionLock>
{
    public ExecutionLock(string key)
    {
        Key = string.IsNullOrWhiteSpace(key)
            ? throw new ArgumentException("Execution lock key is required.", nameof(key))
            : key;
    }

    /// <summary>
    /// Gets the normalized runtime key used by the in-process semaphore table.
    /// </summary>
    public string Key { get; }

    public bool Equals(ExecutionLock? other)
    {
        return other != null && string.Equals(Key, other.Key, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as ExecutionLock);
    }

    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(Key);
    }
}
