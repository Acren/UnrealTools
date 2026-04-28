using System;
using System.Collections.Generic;
using System.Linq;

namespace LocalAutomation.Core.IO;

/// <summary>
/// Describes one relative file or directory entry to materialize from a source root into a destination root.
/// </summary>
public sealed class FileMaterializationEntry
{
    /// <summary>
    /// Stores one materialization entry together with whether missing input should fail and which descendants to skip.
    /// </summary>
    internal FileMaterializationEntry(string relativePath, bool required = false, IEnumerable<string>? excludedRelativePaths = null)
    {
        RelativePath = relativePath ?? throw new ArgumentNullException(nameof(relativePath));
        Required = required;
        ExcludedRelativePaths = (excludedRelativePaths ?? Enumerable.Empty<string>()).ToList();
    }

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
