using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace LocalAutomation.Core.IO;

/// <summary>
/// Collects the ordered include, sync, and preserve entries that materialize one source root into one destination root.
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
        AddEntry(FileMaterializationEntryKind.Include, relativePath, required, excludedRelativePaths: null);
    }

    /// <summary>
    /// Adds one relative directory path whose copied descendants may omit specific source-relative paths.
    /// </summary>
    public void AddDirectory(string relativePath, bool required = false, IEnumerable<string>? excludedRelativePaths = null)
    {
        AddEntry(FileMaterializationEntryKind.Include, relativePath, required, excludedRelativePaths);
    }

    /// <summary>
    /// Adds the materialization root itself as a directory entry, optionally omitting root-relative descendants.
    /// </summary>
    public void AddRootDirectory(bool required = false, IEnumerable<string>? excludedRelativePaths = null)
    {
        AddDirectory(".", required, excludedRelativePaths);
    }

    /// <summary>
    /// Marks one destination-relative directory as synchronized, allowing materialization to delete unmentioned children.
    /// </summary>
    public void Sync(string relativePath)
    {
        AddEntry(FileMaterializationEntryKind.Sync, relativePath, required: false, excludedRelativePaths: null);
    }

    /// <summary>
    /// Marks one destination-relative path as safe to keep inside synchronized roots without copying it from the source.
    /// </summary>
    public void Preserve(string relativePath)
    {
        AddEntry(FileMaterializationEntryKind.Preserve, relativePath, required: false, excludedRelativePaths: null);
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

        /* Subtree expansion keeps every operation in one entry model so include, sync, and preserve semantics compose
           without teaching the generic copy helper about higher-level content types such as Unreal plugins. */
        foreach (FileMaterializationEntry entry in subtreeSpec.Entries)
        {
            AddEntry(entry.Kind, CombineSubtreePath(relativeRootPath, entry.RelativePath), entry.Required, entry.ExcludedRelativePaths);
        }
    }

    /// <summary>
    /// Combines a subtree root with a nested spec path while preserving the subtree root for nested root entries.
    /// </summary>
    private static string CombineSubtreePath(string relativeRootPath, string relativePath)
    {
        return relativePath == "." ? relativeRootPath : Path.Combine(relativeRootPath, relativePath);
    }

    /// <summary>
    /// Appends one entry after public methods have selected the entry kind and include-only options.
    /// </summary>
    private void AddEntry(FileMaterializationEntryKind kind, string relativePath, bool required, IEnumerable<string>? excludedRelativePaths)
    {
        Entries.Add(new FileMaterializationEntry(kind, relativePath, required, excludedRelativePaths));
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
