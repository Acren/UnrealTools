using System.IO;
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
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sourcePath = NormalizeDirectoryPath(sourcePath);
        destinationPath = NormalizeDirectoryPath(destinationPath);
        if (WindowsDirectoryCopy.TryCopy(sourcePath, destinationPath, cancellationToken))
        {
            return;
        }

        ManagedDirectoryCopy.Copy(sourcePath, destinationPath, cancellationToken);
    }

    /// <summary>
    /// Normalizes directory arguments once so copy implementations receive stable paths without trailing separators.
    /// </summary>
    private static string NormalizeDirectoryPath(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
