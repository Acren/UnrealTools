using System;

namespace LocalAutomation.Core.IO;

/// <summary>
/// Describes one relative file or directory entry to materialize from a source root into a destination root.
/// </summary>
public sealed class FileMaterializationEntry
{
    /// <summary>
    /// Stores one materialization entry together with whether missing input should fail the materialization.
    /// </summary>
    public FileMaterializationEntry(string relativePath, bool required = false)
    {
        RelativePath = relativePath ?? throw new ArgumentNullException(nameof(relativePath));
        Required = required;
    }

    /// <summary>
    /// Gets the path relative to the materialization root.
    /// </summary>
    public string RelativePath { get; }

    /// <summary>
    /// Gets whether the source path must exist for materialization to succeed.
    /// </summary>
    public bool Required { get; }
}
