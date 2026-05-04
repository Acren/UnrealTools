using System;
using System.Collections.Generic;
using System.Linq;

namespace LocalAutomation.Core.IO;

/// <summary>
/// Identifies how one materialization entry participates in destination reconciliation.
/// </summary>
public enum FileMaterializationEntryKind
{
    /// <summary>
    /// Copies or mirrors a source-relative file or directory into the destination root.
    /// </summary>
    Include,

    /// <summary>
    /// Marks a destination-relative directory as a pruning boundary for unmentioned paths.
    /// </summary>
    Sync,

    /// <summary>
    /// Protects a destination-relative path inside synchronized roots without copying it from the source.
    /// </summary>
    Preserve
}

/// <summary>
/// Describes one relative path operation used to materialize from a source root into a destination root.
/// </summary>
public sealed class FileMaterializationEntry
{
    /// <summary>
    /// Stores one materialization entry together with whether missing input should fail and which descendants to skip.
    /// </summary>
    internal FileMaterializationEntry(FileMaterializationEntryKind kind, string relativePath, bool required = false, IEnumerable<string>? excludedRelativePaths = null)
    {
        List<string> resolvedExcludedRelativePaths = (excludedRelativePaths ?? Enumerable.Empty<string>()).ToList();
        if (kind != FileMaterializationEntryKind.Include && (required || resolvedExcludedRelativePaths.Count > 0))
        {
            throw new ArgumentException("Only include materialization entries can be required or define excluded paths.", nameof(kind));
        }

        Kind = kind;
        RelativePath = relativePath ?? throw new ArgumentNullException(nameof(relativePath));
        Required = required;
        ExcludedRelativePaths = resolvedExcludedRelativePaths;
    }

    /// <summary>
    /// Gets how this entry participates in materialization and pruning.
    /// </summary>
    public FileMaterializationEntryKind Kind { get; }

    /// <summary>
    /// Gets the path relative to the materialization root.
    /// </summary>
    public string RelativePath { get; }

    /// <summary>
    /// Gets whether the source path must exist for materialization to succeed.
    /// </summary>
    public bool Required { get; }

    /// <summary>
    /// Gets paths relative to this entry's copied directory that should be omitted from the materialization.
    /// </summary>
    public IReadOnlyList<string> ExcludedRelativePaths { get; }
}
