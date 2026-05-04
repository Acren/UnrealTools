using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Core.IO;

public static partial class FileUtils
{
    /// <summary>
    /// Caps concurrent materialization entries so external copy tools cannot multiply without bound.
    /// </summary>
    private const int MaxConcurrentMaterializationEntries = 2;

    /// <summary>
    /// Caps explicit file-set copies so small package files can copy concurrently without unbounded I/O fan-out.
    /// </summary>
    private const int MaxConcurrentMaterializedFileCopies = 32;

    /// <summary>
    /// Describes one copied or preserved path that keeps matching destination content inside a synchronized root.
    /// </summary>
    private readonly record struct SyncCoverageRule(string RelativePath, bool Recursive, IReadOnlyList<string> ExcludedRelativePaths, string? SourceDirectoryPath);

    /// <summary>
    /// Materializes one explicit subset of files and directories from a source root into a destination root.
    /// </summary>
    public static void MaterializeDirectory(
        string sourceRootPath,
        string destinationRootPath,
        FileMaterializationSpec spec,
        ILogger logger,
        CancellationToken cancellationToken = default,
        bool mirrorDirectories = false)
    {
        if (spec == null)
        {
            throw new ArgumentNullException(nameof(spec));
        }

        if (logger == null)
        {
            throw new ArgumentNullException(nameof(logger));
        }

        sourceRootPath = Path.GetFullPath(sourceRootPath);
        destinationRootPath = Path.GetFullPath(destinationRootPath);
        Directory.CreateDirectory(destinationRootPath);

        // Sync roots are explicit deletion boundaries: only paths represented by entries or preserves survive there.
        PruneSynchronizedRoots(sourceRootPath, destinationRootPath, spec, logger, cancellationToken);

        // Each spec entry remains the copy unit so the materialization contract stays direct and predictable.
        Parallel.ForEach(
            spec.Entries.Where(entry => entry.Kind == FileMaterializationEntryKind.Include),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaxConcurrentMaterializationEntries
            },
            entry => MaterializeEntry(sourceRootPath, destinationRootPath, entry, logger, cancellationToken, mirrorDirectories));
    }

    /// <summary>
    /// Deletes destination files and directories beneath synchronized roots when no entry or preserve rule covers them.
    /// </summary>
    private static void PruneSynchronizedRoots(string sourceRootPath, string destinationRootPath, FileMaterializationSpec spec, ILogger logger, CancellationToken cancellationToken)
    {
        IEnumerable<string> syncRoots = spec.Entries
            .Where(entry => entry.Kind == FileMaterializationEntryKind.Sync)
            .Select(entry => entry.RelativePath);
        foreach (string syncRoot in NormalizeMaterializedRelativePaths(destinationRootPath, syncRoots, allowRoot: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destinationSyncRootPath = Path.Combine(destinationRootPath, DenormalizeRelativePath(syncRoot));
            if (File.Exists(destinationSyncRootPath))
            {
                DeleteDestinationEntry(destinationSyncRootPath, logger);
                continue;
            }

            if (!Directory.Exists(destinationSyncRootPath))
            {
                continue;
            }

            IReadOnlyList<SyncCoverageRule> coverage = BuildSyncCoverage(sourceRootPath, destinationRootPath, spec, syncRoot);
            if (coverage.Any(rule => rule.Recursive && rule.RelativePath == "." && rule.ExcludedRelativePaths.Count == 0 && rule.SourceDirectoryPath == null))
            {
                continue;
            }

            logger.LogInformation("Pruning synchronized directory '{RelativePath}' at '{DestinationPath}'.", syncRoot, destinationSyncRootPath);
            PruneSynchronizedDirectory(destinationSyncRootPath, destinationSyncRootPath, coverage, logger, cancellationToken);
        }
    }

    /// <summary>
    /// Builds the set of copied and preserved paths that define what may remain inside one synchronized root.
    /// </summary>
    private static IReadOnlyList<SyncCoverageRule> BuildSyncCoverage(string sourceRootPath, string destinationRootPath, FileMaterializationSpec spec, string syncRoot)
    {
        List<SyncCoverageRule> coverage = new();
        foreach (FileMaterializationEntry entry in spec.Entries.Where(entry => entry.Kind == FileMaterializationEntryKind.Include))
        {
            string entryPath = NormalizeMaterializedRelativePath(destinationRootPath, entry.RelativePath, allowRoot: true);
            if (TryMakeSyncRelativePath(entryPath, syncRoot, out string syncRelativePath))
            {
                // Directory entries cover their subtree; file entries cover only the exact file they materialize.
                bool recursive = Directory.Exists(Path.Combine(sourceRootPath, DenormalizeRelativePath(entryPath)));
                string? sourceDirectoryPath = recursive ? Path.Combine(sourceRootPath, DenormalizeRelativePath(entryPath)) : null;
                IReadOnlyList<string> excludedRelativePaths = recursive
                    ? NormalizeSyncExcludedPaths(destinationRootPath, entryPath, syncRoot, entry.ExcludedRelativePaths)
                    : Array.Empty<string>();
                coverage.Add(new SyncCoverageRule(syncRelativePath, recursive, excludedRelativePaths, sourceDirectoryPath));
            }
        }

        IEnumerable<string> preservedPaths = spec.Entries
            .Where(entry => entry.Kind == FileMaterializationEntryKind.Preserve)
            .Select(entry => entry.RelativePath);
        foreach (string preservedPath in NormalizeMaterializedRelativePaths(destinationRootPath, preservedPaths, allowRoot: true))
        {
            if (TryMakeSyncRelativePath(preservedPath, syncRoot, out string syncRelativePath))
            {
                // Preserve rules are always recursive because they protect generated/cache subtrees from pruning.
                coverage.Add(new SyncCoverageRule(syncRelativePath, true, Array.Empty<string>(), SourceDirectoryPath: null));
            }
        }

        return coverage
            .Distinct()
            .OrderBy(rule => rule.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Converts directory-entry exclusions into synchronized-root-relative paths so pruning can still remove them.
    /// </summary>
    private static IReadOnlyList<string> NormalizeSyncExcludedPaths(string destinationRootPath, string entryPath, string syncRoot, IReadOnlyList<string> excludedRelativePaths)
    {
        List<string> syncExcludedPaths = new();
        foreach (string excludedRelativePath in excludedRelativePaths)
        {
            string excludedPath = NormalizeMaterializedRelativePath(destinationRootPath, Path.Combine(DenormalizeRelativePath(entryPath), excludedRelativePath), allowRoot: true);
            if (TryMakeSyncRelativePath(excludedPath, syncRoot, out string syncRelativePath))
            {
                syncExcludedPaths.Add(syncRelativePath);
            }
        }

        return syncExcludedPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Recursively prunes one synchronized destination directory while retaining covered paths and their ancestors.
    /// </summary>
    private static void PruneSynchronizedDirectory(string syncRootPath, string currentDirectoryPath, IReadOnlyList<SyncCoverageRule> coverage, ILogger logger, CancellationToken cancellationToken)
    {
        foreach (string childPath in Directory.EnumerateFileSystemEntries(currentDirectoryPath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = NormalizeRelativePathForComparison(Path.GetRelativePath(syncRootPath, childPath));
            bool isDirectory = Directory.Exists(childPath);
            if (ConflictsWithRecursiveSource(relativePath, isDirectory, coverage))
            {
                DeleteDestinationEntry(childPath, logger);
                continue;
            }

            if (IsFullyCovered(relativePath, isDirectory, coverage))
            {
                continue;
            }

            if (isDirectory && HasCoveredDescendant(relativePath, coverage))
            {
                PruneSynchronizedDirectory(syncRootPath, childPath, coverage, logger, cancellationToken);
                continue;
            }

            if (IsExactlyCovered(relativePath, coverage))
            {
                continue;
            }

            DeleteDestinationEntry(childPath, logger);
        }
    }

    /// <summary>
    /// Returns whether a destination entry type conflicts with the source path covered by a recursive directory entry.
    /// </summary>
    private static bool ConflictsWithRecursiveSource(string relativePath, bool isDirectory, IReadOnlyList<SyncCoverageRule> coverage)
    {
        foreach (SyncCoverageRule rule in coverage)
        {
            if (!rule.Recursive || rule.SourceDirectoryPath == null || !TryMakeSyncRelativePath(relativePath, rule.RelativePath, out string sourceRelativePath))
            {
                continue;
            }

            if (IsExcludedByRule(relativePath, rule))
            {
                continue;
            }

            string sourcePath = Path.Combine(rule.SourceDirectoryPath, DenormalizeRelativePath(sourceRelativePath));
            if (isDirectory && File.Exists(sourcePath)
                || !isDirectory && Directory.Exists(sourcePath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns whether a coverage rule protects the requested destination entry without needing deeper pruning.
    /// </summary>
    private static bool IsFullyCovered(string relativePath, bool isDirectory, IReadOnlyList<SyncCoverageRule> coverage)
    {
        foreach (SyncCoverageRule rule in coverage)
        {
            if (!rule.Recursive || !IsSameOrDescendantRelativePath(rule.RelativePath, relativePath))
            {
                continue;
            }

            if (IsExcludedByRule(relativePath, rule))
            {
                continue;
            }

            if (rule.SourceDirectoryPath != null && !isDirectory && string.Equals(rule.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            {
                // A source directory cannot protect a destination file occupying the same path.
                continue;
            }

            if (isDirectory && HasExcludedDescendant(relativePath, rule))
            {
                // Directories containing excluded descendants need recursive pruning instead of wholesale preservation.
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns whether a coverage rule protects exactly the requested path without implying descendant coverage.
    /// </summary>
    private static bool IsExactlyCovered(string relativePath, IReadOnlyList<SyncCoverageRule> coverage)
    {
        return coverage.Any(rule => !rule.Recursive && string.Equals(rule.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns whether the current directory is an ancestor needed to reach a covered path.
    /// </summary>
    private static bool HasCoveredDescendant(string relativePath, IReadOnlyList<SyncCoverageRule> coverage)
    {
        string prefix = relativePath + "/";
        return coverage.Any(rule => rule.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || rule.Recursive && IsSameOrDescendantRelativePath(rule.RelativePath, relativePath) && !IsExcludedByRule(relativePath, rule));
    }

    /// <summary>
    /// Returns whether the requested path is excluded from one recursive coverage rule.
    /// </summary>
    private static bool IsExcludedByRule(string relativePath, SyncCoverageRule rule)
    {
        return rule.ExcludedRelativePaths.Any(excludedPath => IsSameOrDescendantRelativePath(excludedPath, relativePath));
    }

    /// <summary>
    /// Returns whether a directory contains an excluded descendant from one recursive coverage rule.
    /// </summary>
    private static bool HasExcludedDescendant(string relativePath, SyncCoverageRule rule)
    {
        return rule.ExcludedRelativePaths.Any(excludedPath => IsSameOrDescendantRelativePath(relativePath, excludedPath));
    }

    /// <summary>
    /// Converts one materialization-relative path into a path relative to a synchronized root when it lives under that root.
    /// </summary>
    private static bool TryMakeSyncRelativePath(string relativePath, string syncRoot, out string syncRelativePath)
    {
        if (syncRoot == ".")
        {
            syncRelativePath = relativePath;
            return true;
        }

        if (string.Equals(relativePath, syncRoot, StringComparison.OrdinalIgnoreCase))
        {
            syncRelativePath = ".";
            return true;
        }

        string prefix = syncRoot + "/";
        if (relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            syncRelativePath = relativePath[prefix.Length..];
            return true;
        }

        syncRelativePath = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns whether the candidate relative path is the covered path itself or a descendant of that path.
    /// </summary>
    private static bool IsSameOrDescendantRelativePath(string ancestorPath, string candidatePath)
    {
        return ancestorPath == "."
            || string.Equals(ancestorPath, candidatePath, StringComparison.OrdinalIgnoreCase)
            || candidatePath.StartsWith(ancestorPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Materializes an explicit set of source-relative files from one root into another root in parallel.
    /// </summary>
    public static void MaterializeFiles(
        string sourceRootPath,
        string destinationRootPath,
        IEnumerable<string> relativeFilePaths,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (relativeFilePaths == null)
        {
            throw new ArgumentNullException(nameof(relativeFilePaths));
        }

        if (logger == null)
        {
            throw new ArgumentNullException(nameof(logger));
        }

        sourceRootPath = Path.GetFullPath(sourceRootPath);
        destinationRootPath = Path.GetFullPath(destinationRootPath);
        IReadOnlyList<string> normalizedRelativeFilePaths = NormalizeMaterializedFilePaths(sourceRootPath, relativeFilePaths);
        Directory.CreateDirectory(destinationRootPath);
        Stopwatch stopwatch = Stopwatch.StartNew();
        logger.LogInformation("Copying {FileCount} file(s) from '{SourceRootPath}' to '{DestinationRootPath}'.", normalizedRelativeFilePaths.Count, sourceRootPath, destinationRootPath);

        // Package payloads are often many small files; direct parallel file copies avoid serial per-file latency.
        Parallel.ForEach(
            normalizedRelativeFilePaths,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaxConcurrentMaterializedFileCopies
            },
            relativeFilePath => CopyMaterializedFile(Path.Combine(sourceRootPath, relativeFilePath), Path.Combine(destinationRootPath, relativeFilePath), cancellationToken));

        stopwatch.Stop();
        logger.LogInformation("Copied {FileCount} file(s) in {Elapsed}.", normalizedRelativeFilePaths.Count, DurationFormatting.FormatSeconds(stopwatch.Elapsed));
    }

    /// <summary>
    /// Materializes one explicit file or directory entry into the destination root.
    /// </summary>
    private static void MaterializeEntry(string sourceRootPath, string destinationRootPath, FileMaterializationEntry entry, ILogger logger, CancellationToken cancellationToken, bool mirrorDirectories)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Kind != FileMaterializationEntryKind.Include)
        {
            throw new InvalidOperationException($"Only include entries can be copied during materialization: {entry.RelativePath}");
        }

        // Include entries use the same normalized relative path for source reads, destination writes, and stale deletes.
        string entryPath = NormalizeMaterializedRelativePath(sourceRootPath, entry.RelativePath, allowRoot: true);
        string filesystemEntryPath = DenormalizeRelativePath(entryPath);
        string sourcePath = Path.Combine(sourceRootPath, filesystemEntryPath);
        string destinationPath = Path.Combine(destinationRootPath, filesystemEntryPath);
        Stopwatch stopwatch = Stopwatch.StartNew();

        // Directory entries use shared helpers so Windows keeps the robocopy fast path for both overlay and mirror modes.
        if (Directory.Exists(sourcePath))
        {
            string verb = mirrorDirectories ? "Mirroring" : "Copying";
            string completedVerb = mirrorDirectories ? "Mirrored" : "Copied";
            logger.LogInformation("{Verb} directory entry '{RelativePath}' from '{SourcePath}' to '{DestinationPath}'.", verb, entryPath, sourcePath, destinationPath);
            if (mirrorDirectories)
            {
                MirrorDirectory(sourcePath, destinationPath, excludedRelativePaths: entry.ExcludedRelativePaths, cancellationToken: cancellationToken);
            }
            else
            {
                CopyDirectory(sourcePath, destinationPath, excludedRelativePaths: entry.ExcludedRelativePaths, cancellationToken: cancellationToken);
            }

            stopwatch.Stop();
            logger.LogInformation("{CompletedVerb} directory entry '{RelativePath}' in {Elapsed}.", completedVerb, entryPath, DurationFormatting.FormatSeconds(stopwatch.Elapsed));
            return;
        }

        // File entries create their parent directory on demand and preserve source timestamps for build-cache inputs.
        if (File.Exists(sourcePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.ExcludedRelativePaths.Count > 0)
            {
                throw new InvalidOperationException($"File materialization entry cannot define excluded paths: {entryPath}");
            }

            logger.LogInformation("Copying file entry '{RelativePath}' from '{SourcePath}' to '{DestinationPath}'.", entryPath, sourcePath, destinationPath);
            CopyMaterializedFile(sourcePath, destinationPath, cancellationToken);
            stopwatch.Stop();
            logger.LogInformation("Copied file entry '{RelativePath}' in {Elapsed}.", entryPath, DurationFormatting.FormatSeconds(stopwatch.Elapsed));
            return;
        }

        // Required entries fail loudly so incomplete materializations remain visible even when sibling tasks run.
        if (entry.Required)
        {
            throw new FileNotFoundException($"Required materialization entry is missing: {sourcePath}", sourcePath);
        }

        // Mirror mode treats absent optional entries as stale destination inputs; overlay mode keeps the historic no-op.
        if (mirrorDirectories)
        {
            DeleteDestinationEntry(destinationPath, logger);
        }
    }

    /// <summary>
    /// Normalizes explicit file entries and rejects paths that are empty, rooted, or escape the source root.
    /// </summary>
    private static IReadOnlyList<string> NormalizeMaterializedFilePaths(string sourceRootPath, IEnumerable<string> relativeFilePaths)
    {
        return NormalizeMaterializedRelativePaths(sourceRootPath, relativeFilePaths, allowRoot: false);
    }

    /// <summary>
    /// Normalizes explicit relative paths and rejects paths that are empty, rooted, or escape the root.
    /// </summary>
    private static IReadOnlyList<string> NormalizeMaterializedRelativePaths(string rootPath, IEnumerable<string> relativePaths, bool allowRoot)
    {
        return relativePaths
            .Select(relativePath => NormalizeMaterializedRelativePath(rootPath, relativePath, allowRoot))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(relativePath => relativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Normalizes one explicit relative path while enforcing the requested root boundary.
    /// </summary>
    private static string NormalizeMaterializedRelativePath(string rootPath, string relativePath, bool allowRoot)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Materialized paths must be non-empty relative paths.", nameof(relativePath));
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException($"Materialized path must be relative: {relativePath}", nameof(relativePath));
        }

        string absolutePath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        string normalizedRelativePath = Path.GetRelativePath(rootPath, absolutePath);
        if (normalizedRelativePath == ".."
            || normalizedRelativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || normalizedRelativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(normalizedRelativePath))
        {
            throw new ArgumentException($"Materialized path must stay inside the root directory: {relativePath}", nameof(relativePath));
        }

        if (normalizedRelativePath == ".")
        {
            if (allowRoot)
            {
                return ".";
            }

            throw new ArgumentException("Materialized file paths cannot refer to the root directory itself.", nameof(relativePath));
        }

        return NormalizeRelativePathForComparison(normalizedRelativePath);
    }

    /// <summary>
    /// Converts normalized comparison paths back into platform paths for filesystem operations.
    /// </summary>
    private static string DenormalizeRelativePath(string relativePath)
    {
        return relativePath == "." ? "." : relativePath.Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Converts platform separators to one separator so relative path prefix checks stay reliable.
    /// </summary>
    private static string NormalizeRelativePathForComparison(string relativePath)
    {
        return relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').TrimEnd('/');
    }

    /// <summary>
    /// Copies one file entry and applies the source timestamp so timestamp-sensitive tools see the original input time.
    /// </summary>
    private static void CopyMaterializedFile(string sourceFilePath, string destinationFilePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? destinationDirectoryPath = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
        {
            Directory.CreateDirectory(destinationDirectoryPath);
        }

        if (Directory.Exists(destinationFilePath))
        {
            DeleteDirectoryIfExists(destinationFilePath);
        }

        File.Copy(sourceFilePath, destinationFilePath, true);
        File.SetLastWriteTimeUtc(destinationFilePath, File.GetLastWriteTimeUtc(sourceFilePath));
    }

    /// <summary>
    /// Deletes one destination file or directory entry when the matching optional source entry is absent.
    /// </summary>
    private static void DeleteDestinationEntry(string destinationPath, ILogger logger)
    {
        if (File.Exists(destinationPath))
        {
            logger.LogInformation("Deleting stale file entry: {FilePath}", destinationPath);
            DeleteFileIfExists(destinationPath);
            return;
        }

        DeleteDirectoryIfExists(destinationPath, logger);
    }
}
