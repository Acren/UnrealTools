using System;
using System.IO;

namespace LocalAutomation.Core.IO;

public static partial class FileUtils
{
    /// <summary>
    /// Materializes one explicit subset of files and directories from a source root into a destination root.
    /// </summary>
    public static void MaterializeDirectory(string sourceRootPath, string destinationRootPath, FileMaterializationSpec spec)
    {
        if (spec == null)
        {
            throw new ArgumentNullException(nameof(spec));
        }

        sourceRootPath = Path.GetFullPath(sourceRootPath);
        destinationRootPath = Path.GetFullPath(destinationRootPath);
        Directory.CreateDirectory(destinationRootPath);

        /* Materialize each requested entry independently so one spec can mix files, directories, and required guards. */
        foreach (FileMaterializationEntry entry in spec.Entries)
        {
            string sourcePath = Path.Combine(sourceRootPath, entry.RelativePath);
            string destinationPath = Path.Combine(destinationRootPath, entry.RelativePath);

            if (Directory.Exists(sourcePath))
            {
                CopyDirectory(sourcePath, destinationPath);
                continue;
            }

            if (File.Exists(sourcePath))
            {
                string? destinationDirectoryPath = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
                {
                    Directory.CreateDirectory(destinationDirectoryPath);
                }

                File.Copy(sourcePath, destinationPath, true);
                continue;
            }

            if (entry.Required)
            {
                throw new FileNotFoundException($"Required materialization entry is missing: {sourcePath}", sourcePath);
            }
        }
    }
}
