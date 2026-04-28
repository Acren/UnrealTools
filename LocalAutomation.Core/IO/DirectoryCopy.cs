using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace LocalAutomation.Core.IO;

/// <summary>
/// Chooses the best available directory-copy implementation for the current platform while preserving one stable API.
/// </summary>
internal static class DirectoryCopy
{
    /// <summary>
    /// Copies one directory tree to another destination, preferring the platform fast path when available.
    /// </summary>
    public static void Copy(
        string sourcePath,
        string destinationPath,
        IEnumerable<string>? excludedRelativePaths = null,
        CancellationToken cancellationToken = default)
    {
        Run(sourcePath, destinationPath, excludedRelativePaths, cancellationToken, WindowsDirectoryCopy.TryCopy, ManagedDirectoryCopy.Copy);
    }

    /// <summary>
    /// Mirrors one directory tree to another destination, preferring the platform fast path when available.
    /// </summary>
    public static void Mirror(
        string sourcePath,
        string destinationPath,
        IEnumerable<string>? excludedRelativePaths = null,
        CancellationToken cancellationToken = default)
    {
        Run(sourcePath, destinationPath, excludedRelativePaths, cancellationToken, WindowsDirectoryCopy.TryMirror, ManagedDirectoryCopy.Mirror);
    }

    /// <summary>
    /// Normalizes one directory operation and dispatches to the platform implementation before the portable fallback.
    /// </summary>
    private static void Run(
        string sourcePath,
        string destinationPath,
        IEnumerable<string>? excludedRelativePaths,
        CancellationToken cancellationToken,
        Func<string, string, IReadOnlyList<string>, CancellationToken, bool> tryPlatformCopy,
        Action<string, string, IReadOnlyList<string>, CancellationToken> managedCopy)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sourcePath = NormalizeDirectoryPath(sourcePath);
        destinationPath = NormalizeDirectoryPath(destinationPath);
        IReadOnlyList<string> normalizedExcludedRelativePaths = NormalizeExcludedRelativePaths(sourcePath, excludedRelativePaths);
        if (tryPlatformCopy(sourcePath, destinationPath, normalizedExcludedRelativePaths, cancellationToken))
        {
            return;
        }

        managedCopy(sourcePath, destinationPath, normalizedExcludedRelativePaths, cancellationToken);
    }

    /// <summary>
    /// Normalizes exclude paths so platform-specific copy implementations can safely compare them as source-relative paths.
    /// </summary>
    internal static IReadOnlyList<string> NormalizeExcludedRelativePaths(string sourcePath, IEnumerable<string>? excludedRelativePaths)
    {
        if (excludedRelativePaths == null)
        {
            return Array.Empty<string>();
        }

        string normalizedSourcePath = NormalizeDirectoryPath(sourcePath);
        return excludedRelativePaths
            .Select(excludedPath => NormalizeExcludedRelativePath(normalizedSourcePath, excludedPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(excludedPath => excludedPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Converts one caller-provided exclude into a safe relative path that cannot escape the copied source directory.
    /// </summary>
    private static string NormalizeExcludedRelativePath(string sourcePath, string excludedRelativePath)
    {
        if (string.IsNullOrWhiteSpace(excludedRelativePath))
        {
            throw new ArgumentException("Excluded copy paths must be non-empty relative paths.", nameof(excludedRelativePath));
        }

        if (Path.IsPathRooted(excludedRelativePath))
        {
            throw new ArgumentException($"Excluded copy path must be relative: {excludedRelativePath}", nameof(excludedRelativePath));
        }

        string absoluteExcludedPath = Path.GetFullPath(Path.Combine(sourcePath, excludedRelativePath));
        if (!IsSameOrDescendantPath(sourcePath, absoluteExcludedPath))
        {
            throw new ArgumentException($"Excluded copy path must stay inside the source directory: {excludedRelativePath}", nameof(excludedRelativePath));
        }

        if (string.Equals(sourcePath, absoluteExcludedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Excluded copy path cannot be the copied directory itself.", nameof(excludedRelativePath));
        }

        return Path.GetRelativePath(sourcePath, absoluteExcludedPath);
    }

    /// <summary>
    /// Returns whether the candidate path is the source root itself or a descendant of that source root.
    /// </summary>
    private static bool IsSameOrDescendantPath(string sourcePath, string candidatePath)
    {
        string sourceWithSeparator = sourcePath + Path.DirectorySeparatorChar;
        return string.Equals(sourcePath, candidatePath, StringComparison.OrdinalIgnoreCase)
            || candidatePath.StartsWith(sourceWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalizes directory arguments once so copy implementations receive stable paths without trailing separators.
    /// </summary>
    private static string NormalizeDirectoryPath(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
