namespace LocalAutomation.Core.IO;

/// <summary>
/// Chooses the best available directory-copy implementation for the current platform while preserving one stable API.
/// </summary>
internal static class DirectoryCopy
{
    /// <summary>
    /// Copies one directory tree to another destination, preferring the platform fast path when available.
    /// </summary>
    public static void Copy(string sourcePath, string destinationPath)
    {
        if (WindowsDirectoryCopy.TryCopy(sourcePath, destinationPath))
        {
            return;
        }

        ManagedDirectoryCopy.Copy(sourcePath, destinationPath);
    }
}
