using System;
using System.IO;

namespace LocalAutomation.Core.IO;

public static partial class FileUtils
{
    /// <summary>
    /// Copies one directory tree to the destination path, optionally nesting the source directory inside the destination.
    /// </summary>
    public static void CopyDirectory(string sourcePath, string destinationPath, bool placeInside = false)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        destinationPath = Path.GetFullPath(destinationPath);

        if (placeInside)
        {
            string directoryName = new DirectoryInfo(sourcePath).Name;
            destinationPath = Path.Combine(destinationPath, directoryName);
        }

        DirectoryCopy.Copy(sourcePath, destinationPath);
    }

    /// <summary>
    /// Copies one named subdirectory from a source root into the matching location beneath the destination root.
    /// </summary>
    public static void CopySubdirectory(string sourcePath, string destinationPath, string subdirectory)
    {
        Directory.CreateDirectory(Path.Combine(destinationPath, subdirectory));
        CopyDirectory(Path.Combine(sourcePath, subdirectory), Path.Combine(destinationPath, subdirectory));
    }

    /// <summary>
    /// Copies one named file from a source directory into a destination directory.
    /// </summary>
    public static void CopyFile(string sourceDirectoryPath, string destinationDirectoryPath, string fileName, bool errorIfSourceMissing = true, bool overwrite = false)
    {
        CopyFile(Path.Combine(sourceDirectoryPath, fileName), destinationDirectoryPath, errorIfSourceMissing, overwrite);
    }

    /// <summary>
    /// Copies one file into a destination directory, optionally tolerating a missing source or overwriting the target.
    /// </summary>
    public static void CopyFile(string sourceFilePath, string destinationDirectoryPath, bool errorIfSourceMissing = true, bool overwrite = false)
    {
        if (!File.Exists(sourceFilePath) && !errorIfSourceMissing)
        {
            return;
        }

        string fileName = Path.GetFileName(sourceFilePath);
        string destinationFilePath = Path.Combine(destinationDirectoryPath, fileName);
        if (overwrite)
        {
            DeleteFileIfExists(destinationFilePath);
        }

        File.Copy(sourceFilePath, destinationFilePath);
    }
}
