using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace LocalAutomation.Core.IO;

/// <summary>
/// Implements the portable recursive directory copy fallback used when no platform-specific fast path is available.
/// </summary>
internal static class ManagedDirectoryCopy
{
    /// <summary>
    /// Recursively copies one directory tree to another path using the managed file APIs.
    /// </summary>
    public static void Copy(
        string sourcePath,
        string destinationPath,
        IReadOnlyList<string> excludedRelativePaths,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = excludedRelativePaths ?? throw new ArgumentNullException(nameof(excludedRelativePaths));
        Directory.CreateDirectory(destinationPath);
        CopyDirectoryContents(sourcePath, sourcePath, destinationPath, excludedRelativePaths, cancellationToken);
    }

    /// <summary>
    /// Recursively copies allowed entries while avoiding traversal into excluded directory subtrees.
    /// </summary>
    private static void CopyDirectoryContents(
        string sourceRootPath,
        string currentSourcePath,
        string destinationRootPath,
        IReadOnlyList<string> excludedRelativePaths,
        CancellationToken cancellationToken)
    {
        // Create allowed child directories before recursing so file copies under them always have a destination root.
        foreach (string directoryPath in Directory.GetDirectories(currentSourcePath, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativeDirectoryPath = Path.GetRelativePath(sourceRootPath, directoryPath);
            if (IsExcluded(relativeDirectoryPath, excludedRelativePaths))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(destinationRootPath, relativeDirectoryPath));
            CopyDirectoryContents(sourceRootPath, directoryPath, destinationRootPath, excludedRelativePaths, cancellationToken);
        }

        // Copy allowed files with overwrite enabled so repeated materializations can refresh an existing destination tree.
        foreach (string filePath in Directory.GetFiles(currentSourcePath, "*.*", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativeFilePath = Path.GetRelativePath(sourceRootPath, filePath);
            if (IsExcluded(relativeFilePath, excludedRelativePaths))
            {
                continue;
            }

            string destinationFilePath = Path.Combine(destinationRootPath, relativeFilePath);
            string? destinationDirectoryPath = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
            {
                Directory.CreateDirectory(destinationDirectoryPath);
            }

            File.Copy(filePath, destinationFilePath, true);
        }
    }

    /// <summary>
    /// Returns whether a source-relative path is exactly excluded or lives under an excluded subtree.
    /// </summary>
    private static bool IsExcluded(string relativePath, IReadOnlyList<string> excludedRelativePaths)
    {
        string normalizedRelativePath = NormalizeForComparison(relativePath);
        foreach (string excludedRelativePath in excludedRelativePaths)
        {
            string normalizedExcludedRelativePath = NormalizeForComparison(excludedRelativePath);
            if (string.Equals(normalizedRelativePath, normalizedExcludedRelativePath, StringComparison.OrdinalIgnoreCase)
                || normalizedRelativePath.StartsWith(normalizedExcludedRelativePath + '/', StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Converts platform path separators to one stable separator for path-prefix comparisons.
    /// </summary>
    private static string NormalizeForComparison(string relativePath)
    {
        return relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }
}
