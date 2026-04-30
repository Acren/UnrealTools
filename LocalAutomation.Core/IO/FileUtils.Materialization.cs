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

        // Each spec entry remains the copy unit so the materialization contract stays direct and predictable.
        Parallel.ForEach(
            spec.Entries,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaxConcurrentMaterializationEntries
            },
            entry => MaterializeEntry(sourceRootPath, destinationRootPath, entry, logger, cancellationToken, mirrorDirectories));
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
        string sourcePath = Path.Combine(sourceRootPath, entry.RelativePath);
        string destinationPath = Path.Combine(destinationRootPath, entry.RelativePath);
        Stopwatch stopwatch = Stopwatch.StartNew();

        // Directory entries use shared helpers so Windows keeps the robocopy fast path for both overlay and mirror modes.
        if (Directory.Exists(sourcePath))
        {
            string verb = mirrorDirectories ? "Mirroring" : "Copying";
            string completedVerb = mirrorDirectories ? "Mirrored" : "Copied";
            logger.LogInformation("{Verb} directory entry '{RelativePath}' from '{SourcePath}' to '{DestinationPath}'.", verb, entry.RelativePath, sourcePath, destinationPath);
            if (mirrorDirectories)
            {
                MirrorDirectory(sourcePath, destinationPath, excludedRelativePaths: entry.ExcludedRelativePaths, cancellationToken: cancellationToken);
            }
            else
            {
                CopyDirectory(sourcePath, destinationPath, excludedRelativePaths: entry.ExcludedRelativePaths, cancellationToken: cancellationToken);
            }

            stopwatch.Stop();
            logger.LogInformation("{CompletedVerb} directory entry '{RelativePath}' in {Elapsed}.", completedVerb, entry.RelativePath, DurationFormatting.FormatSeconds(stopwatch.Elapsed));
            return;
        }

        // File entries create their parent directory on demand and preserve source timestamps for build-cache inputs.
        if (File.Exists(sourcePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.ExcludedRelativePaths.Count > 0)
            {
                throw new InvalidOperationException($"File materialization entry cannot define excluded paths: {entry.RelativePath}");
            }

            logger.LogInformation("Copying file entry '{RelativePath}' from '{SourcePath}' to '{DestinationPath}'.", entry.RelativePath, sourcePath, destinationPath);
            CopyMaterializedFile(sourcePath, destinationPath, cancellationToken);
            stopwatch.Stop();
            logger.LogInformation("Copied file entry '{RelativePath}' in {Elapsed}.", entry.RelativePath, DurationFormatting.FormatSeconds(stopwatch.Elapsed));
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
        return relativeFilePaths
            .Select(relativeFilePath => NormalizeMaterializedFilePath(sourceRootPath, relativeFilePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(relativeFilePath => relativeFilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Normalizes one explicit source-relative file path while enforcing the source-root boundary.
    /// </summary>
    private static string NormalizeMaterializedFilePath(string sourceRootPath, string relativeFilePath)
    {
        if (string.IsNullOrWhiteSpace(relativeFilePath))
        {
            throw new ArgumentException("Materialized file paths must be non-empty relative paths.", nameof(relativeFilePath));
        }

        if (Path.IsPathRooted(relativeFilePath))
        {
            throw new ArgumentException($"Materialized file path must be relative: {relativeFilePath}", nameof(relativeFilePath));
        }

        string absoluteFilePath = Path.GetFullPath(Path.Combine(sourceRootPath, relativeFilePath));
        string normalizedRelativeFilePath = Path.GetRelativePath(sourceRootPath, absoluteFilePath);
        if (normalizedRelativeFilePath == "."
            || normalizedRelativeFilePath == ".."
            || normalizedRelativeFilePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || normalizedRelativeFilePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(normalizedRelativeFilePath))
        {
            throw new ArgumentException($"Materialized file path must stay inside the source directory: {relativeFilePath}", nameof(relativeFilePath));
        }

        return normalizedRelativeFilePath;
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
