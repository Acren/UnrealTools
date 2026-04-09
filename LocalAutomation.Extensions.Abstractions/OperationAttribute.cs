using System;

namespace LocalAutomation.Extensions.Abstractions;

/// <summary>
/// Declares the stable registration metadata for one user-discoverable runtime operation.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class OperationAttribute : Attribute
{
    /// <summary>
    /// Creates descriptor metadata for one discoverable operation.
    /// </summary>
    public OperationAttribute()
    {
    }

    /// <summary>
    /// Gets the preferred user-facing sort order. Hosts fall back to alphabetical ordering when values collide.
    /// </summary>
    public int SortOrder { get; init; }
}
