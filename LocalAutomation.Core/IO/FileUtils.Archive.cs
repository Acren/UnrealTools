using System.IO;
using System.IO.Compression;

namespace LocalAutomation.Core.IO;

public static partial class FileUtils
{
    /// <summary>
    /// Creates one zip archive from a directory tree.
    /// </summary>
    public static void CreateZipFromDirectory(string sourceDirectory, string destinationArchiveFileName, bool includeBaseDirectory)
    {
        ZipFile.CreateFromDirectory(sourceDirectory, destinationArchiveFileName, CompressionLevel.Optimal, includeBaseDirectory);
    }
}
