using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LocalAutomation.Runtime;

/// <summary>
/// Creates workspace descriptors for session-scoped scratch roots and stable roots that preserve reusable artifacts.
/// </summary>
public static class Workspaces
{
    // Workspace hashes stay short because Unreal-style build trees can already create very deep output paths.
    private const int PersistentHashLength = 16;

    /// <summary>
    /// Wraps one session-owned root path in the same workspace shape used by persistent roots.
    /// </summary>
    public static Workspace Session(string rootPath)
    {
        return new Workspace(rootPath, Array.Empty<ExecutionLock>());
    }

    /// <summary>
    /// Returns the persistent workspace for the provided ordered identity parts.
    /// </summary>
    public static Workspace Persistent(params string[] keyParts)
    {
        return Persistent((IEnumerable<string>)keyParts);
    }

    /// <summary>
    /// Returns the persistent workspace for the provided ordered identity parts without interpreting their meaning.
    /// </summary>
    public static Workspace Persistent(IEnumerable<string> keyParts)
    {
        IReadOnlyList<string> normalizedParts = NormalizeKeyParts(keyParts);
        string cacheKey = ComputeHash(string.Join("|", normalizedParts), PersistentHashLength);
        string workspacePath = Path.Combine(OutputPaths.TempRoot(), "PersistentWorkspaces", cacheKey);
        return new Workspace(workspacePath, new[] { new ExecutionLock($"persistent-workspace:{NormalizePathForLock(workspacePath)}") });
    }

    /// <summary>
    /// Validates identity parts so callers cannot accidentally collapse distinct workspaces through blank key entries.
    /// </summary>
    private static IReadOnlyList<string> NormalizeKeyParts(IEnumerable<string> keyParts)
    {
        if (keyParts == null)
        {
            throw new ArgumentNullException(nameof(keyParts));
        }

        List<string> normalizedParts = keyParts.Select(part => part?.Trim() ?? string.Empty).ToList();
        if (normalizedParts.Count == 0)
        {
            throw new ArgumentException("At least one persistent workspace key part is required.", nameof(keyParts));
        }

        if (normalizedParts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Persistent workspace key parts cannot be null, empty, or whitespace.", nameof(keyParts));
        }

        return normalizedParts;
    }

    /// <summary>
    /// Produces a compact uppercase hash for a validated workspace identity string.
    /// </summary>
    private static string ComputeHash(string source, int length)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(hash).Substring(0, length);
    }

    /// <summary>
    /// Normalizes workspace paths before they become execution-lock keys so equivalent Windows paths serialize together.
    /// </summary>
    private static string NormalizePathForLock(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
    }
}

/// <summary>
/// Describes one filesystem workspace root plus any locks required while mutating that root.
/// </summary>
public sealed class Workspace
{
    /// <summary>
    /// Creates one immutable workspace descriptor from a root path and its mutation locks.
    /// </summary>
    internal Workspace(string rootPath, IReadOnlyList<ExecutionLock> mutationLocks)
    {
        RootPath = string.IsNullOrWhiteSpace(rootPath) ? throw new ArgumentException("Workspace root path is required.", nameof(rootPath)) : rootPath;
        MutationLocks = (mutationLocks ?? throw new ArgumentNullException(nameof(mutationLocks))).ToArray();
    }

    /// <summary>
    /// Gets the directory root where callers materialize inputs, run operations, and read outputs.
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// Gets the locks callers should hold while mutating this workspace root.
    /// </summary>
    public IReadOnlyList<ExecutionLock> MutationLocks { get; }

    /// <summary>
    /// Combines relative path segments under the workspace root, or returns the root when no segments are supplied.
    /// </summary>
    public string GetPath(params string[] segments)
    {
        if (segments == null)
        {
            throw new ArgumentNullException(nameof(segments));
        }

        return segments.Aggregate(RootPath, Path.Combine);
    }
}
