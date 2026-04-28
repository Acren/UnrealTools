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
        AddEntry(relativePath, required, excludedRelativePaths: null);
    }

    /// <summary>
    /// Adds one relative directory path whose copied descendants may omit specific source-relative paths.
    /// </summary>
    public void AddDirectory(string relativePath, bool required = false, IEnumerable<string>? excludedRelativePaths = null)
    {
        AddEntry(relativePath, required, excludedRelativePaths);
    }

    /// <summary>
    /// Adds the materialization root itself as a directory entry, optionally omitting root-relative descendants.
    /// </summary>
    public void AddRootDirectory(bool required = false, IEnumerable<string>? excludedRelativePaths = null)
    {
        AddDirectory(".", required, excludedRelativePaths);
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
            AddEntry(Path.Combine(relativeRootPath, entry.RelativePath), entry.Required, entry.ExcludedRelativePaths);
        }
    }

    /// <summary>
    /// Appends one entry after public methods have selected whether directory-only filtering is allowed.
    /// </summary>
    private void AddEntry(string relativePath, bool required, IEnumerable<string>? excludedRelativePaths)
    {
        Entries.Add(new FileMaterializationEntry(relativePath, required, excludedRelativePaths));
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
