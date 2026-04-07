using System.IO;

namespace LocalAutomation.Core.IO;

/// <summary>
/// Implements the portable recursive directory copy fallback used when no platform-specific fast path is available.
/// </summary>
internal static class ManagedDirectoryCopy
{
    /// <summary>
    /// Recursively copies one directory tree to another path using the managed file APIs.
    /// </summary>
    public static void Copy(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        /* Recreate the directory tree first so later file copies do not need to reason about directory ordering. */
        foreach (string directoryPath in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            string relativeDirectoryPath = Path.GetRelativePath(sourcePath, directoryPath);
            Directory.CreateDirectory(Path.Combine(destinationPath, relativeDirectoryPath));
        }

        /* Copy every file with overwrite enabled so repeated materializations can refresh an existing destination tree. */
        foreach (string filePath in Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories))
        {
            string relativeFilePath = Path.GetRelativePath(sourcePath, filePath);
            string destinationFilePath = Path.Combine(destinationPath, relativeFilePath);
            string? destinationDirectoryPath = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
            {
                Directory.CreateDirectory(destinationDirectoryPath);
            }

            File.Copy(filePath, destinationFilePath, true);
        }
    }
}
