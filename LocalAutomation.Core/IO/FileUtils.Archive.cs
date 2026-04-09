using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Core.IO;

public static partial class FileUtils
{
    /// <summary>
    /// Creates one zip archive from a directory tree.
    /// </summary>
    public static void CreateZipFromDirectory(string sourceDirectory, string destinationArchiveFileName, bool includeBaseDirectory, ILogger logger)
    {
        if (logger == null)
        {
            throw new ArgumentNullException(nameof(logger));
        }

        /* Archive creation is one of the slowest file-system boundaries in deploy flows, so log the exact source and
           destination paths before compression starts. */
        Stopwatch stopwatch = Stopwatch.StartNew();
        logger.LogInformation("Creating zip archive from '{SourceDirectory}' to '{DestinationArchiveFileName}' (includeBaseDirectory: {IncludeBaseDirectory}).", sourceDirectory, destinationArchiveFileName, includeBaseDirectory);
        ZipFile.CreateFromDirectory(sourceDirectory, destinationArchiveFileName, CompressionLevel.Optimal, includeBaseDirectory);
        stopwatch.Stop();
        logger.LogInformation("Created zip archive '{DestinationArchiveFileName}' in {Elapsed}.", destinationArchiveFileName, DurationFormatting.FormatSeconds(stopwatch.Elapsed));
    }
}
