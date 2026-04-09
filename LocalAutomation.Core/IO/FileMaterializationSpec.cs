using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

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
    /// Adds all entries from another materialization spec underneath one relative root path.
    /// </summary>
    public void AddSubtree(string relativeRootPath, FileMaterializationSpec subtreeSpec)
    {
        if (relativeRootPath == null)
        {
            throw new ArgumentNullException(nameof(relativeRootPath));
        }

        if (subtreeSpec == null)
        {
            throw new ArgumentNullException(nameof(subtreeSpec));
        }

        /* Subtree expansion keeps the materialization model explicit: callers can compose nested subsets without
           teaching the generic copy helper about higher-level content types such as Unreal plugins. */
        foreach (FileMaterializationEntry entry in subtreeSpec.Entries)
        {
            Add(Path.Combine(relativeRootPath, entry.RelativePath), entry.Required);
        }
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
