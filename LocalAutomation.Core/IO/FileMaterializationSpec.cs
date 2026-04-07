using System.Collections;
using System.Collections.Generic;

namespace LocalAutomation.Core.IO;

/// <summary>
/// Collects the explicit file and directory entries to materialize from one source root into one destination root.
/// </summary>
public sealed class FileMaterializationSpec : IEnumerable<FileMaterializationEntry>
{
    /// <summary>
    /// Stores the ordered set of materialization entries.
    /// </summary>
    public List<FileMaterializationEntry> Entries { get; } = new();

    /// <summary>
    /// Adds one relative file or directory path to the materialization spec.
    /// </summary>
    public void Add(string relativePath, bool required = false)
    {
        Entries.Add(new FileMaterializationEntry(relativePath, required));
    }

    /// <summary>
    /// Returns the materialization entries in insertion order.
    /// </summary>
    public IEnumerator<FileMaterializationEntry> GetEnumerator() => Entries.GetEnumerator();

    /// <summary>
    /// Returns the nongeneric enumerator for collection consumers.
    /// </summary>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
