using System;
using System.Diagnostics;
using System.IO;
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
    /// Materializes one explicit subset of files and directories from a source root into a destination root.
    /// </summary>
    public static void MaterializeDirectory(string sourceRootPath, string destinationRootPath, FileMaterializationSpec spec, ILogger logger, CancellationToken cancellationToken = default)
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
            entry => MaterializeEntry(sourceRootPath, destinationRootPath, entry, logger, cancellationToken));
    }

    /// <summary>
    /// Materializes one explicit file or directory entry into the destination root.
    /// </summary>
    private static void MaterializeEntry(string sourceRootPath, string destinationRootPath, FileMaterializationEntry entry, ILogger logger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string sourcePath = Path.Combine(sourceRootPath, entry.RelativePath);
        string destinationPath = Path.Combine(destinationRootPath, entry.RelativePath);
        Stopwatch stopwatch = Stopwatch.StartNew();

        // Directory entries use the shared copy helper so Windows gets the robocopy fast path and other hosts fall back.
        if (Directory.Exists(sourcePath))
        {
            logger.LogInformation("Copying directory entry '{RelativePath}' from '{SourcePath}' to '{DestinationPath}'.", entry.RelativePath, sourcePath, destinationPath);
            CopyDirectory(sourcePath, destinationPath, cancellationToken: cancellationToken);
            stopwatch.Stop();
            logger.LogInformation("Copied directory entry '{RelativePath}' in {Elapsed}.", entry.RelativePath, DurationFormatting.FormatSeconds(stopwatch.Elapsed));
            return;
        }

        // File entries create their parent directory on demand because sibling tasks can finish in any order.
        if (File.Exists(sourcePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogInformation("Copying file entry '{RelativePath}' from '{SourcePath}' to '{DestinationPath}'.", entry.RelativePath, sourcePath, destinationPath);
            string? destinationDirectoryPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
            {
                Directory.CreateDirectory(destinationDirectoryPath);
            }

            File.Copy(sourcePath, destinationPath, true);
            stopwatch.Stop();
            logger.LogInformation("Copied file entry '{RelativePath}' in {Elapsed}.", entry.RelativePath, DurationFormatting.FormatSeconds(stopwatch.Elapsed));
            return;
        }

        // Required entries fail loudly so incomplete materializations remain visible even when sibling tasks run.
        if (entry.Required)
        {
            throw new FileNotFoundException($"Required materialization entry is missing: {sourcePath}", sourcePath);
        }
    }
}
