using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Core.IO;

public static partial class FileUtils
{
    /// <summary>
    /// Materializes one explicit subset of files and directories from a source root into a destination root.
    /// </summary>
    public static void MaterializeDirectory(string sourceRootPath, string destinationRootPath, FileMaterializationSpec spec, ILogger logger)
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

        /* Materialize each requested entry independently so explicit include-list specs can fan out across files and
           directories without changing the public API shape. */
        Task[] materializationTasks = spec.Entries
            .Select(entry => Task.Run(() => MaterializeEntry(sourceRootPath, destinationRootPath, entry, logger)))
            .ToArray();
        Task.WhenAll(materializationTasks).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Materializes one explicit file or directory entry into the destination root.
    /// </summary>
    private static void MaterializeEntry(string sourceRootPath, string destinationRootPath, FileMaterializationEntry entry, ILogger logger)
    {
        string sourcePath = Path.Combine(sourceRootPath, entry.RelativePath);
        string destinationPath = Path.Combine(destinationRootPath, entry.RelativePath);
        Stopwatch stopwatch = Stopwatch.StartNew();

        /* Directory entries continue through the existing copy helper so Windows keeps the robocopy fast path while
           non-Windows hosts keep using the managed recursive fallback. */
        if (Directory.Exists(sourcePath))
        {
            logger.LogInformation("Copying directory entry '{RelativePath}' from '{SourcePath}' to '{DestinationPath}'.", entry.RelativePath, sourcePath, destinationPath);
            CopyDirectory(sourcePath, destinationPath);
            stopwatch.Stop();
            logger.LogInformation("Copied directory entry '{RelativePath}' in {Elapsed}.", entry.RelativePath, DurationFormatting.FormatSeconds(stopwatch.Elapsed));
            return;
        }

        /* File entries create their parent directory on demand because sibling tasks may reach separate destination
           branches in any order. */
        if (File.Exists(sourcePath))
        {
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

        /* Required entries still fail loudly so incomplete materializations remain visible even when sibling copy tasks
           are running concurrently. */
        if (entry.Required)
        {
            throw new FileNotFoundException($"Required materialization entry is missing: {sourcePath}", sourcePath);
        }
    }
}
