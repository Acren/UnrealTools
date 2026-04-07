using System;
using System.IO;
using System.Linq;

namespace LocalAutomation.Core.IO;

public static partial class FileUtils
{
    /// <summary>
    /// Deletes one directory tree only when it exists.
    /// </summary>
    public static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            DeleteDirectory(path);
        }
    }

    /// <summary>
    /// Deletes every immediate child directory beneath the top path except the explicitly allowed names.
    /// </summary>
    public static void DeleteOtherSubdirectories(string topPath, string[] subdirectoriesToKeep)
    {
        foreach (string subDirectory in Directory.GetDirectories(topPath, "*", SearchOption.TopDirectoryOnly))
        {
            if (Array.IndexOf(subdirectoriesToKeep, Path.GetFileName(subDirectory)) < 0)
            {
                DeleteDirectoryIfExists(subDirectory);
            }
        }
    }

    /// <summary>
    /// Deletes one file.
    /// </summary>
    public static void DeleteFile(string filePath)
    {
        File.Delete(filePath);
    }

    /// <summary>
    /// Deletes one file only when it exists.
    /// </summary>
    public static void DeleteFileIfExists(string filePath)
    {
        if (File.Exists(filePath))
        {
            DeleteFile(filePath);
        }
    }

    /// <summary>
    /// Deletes every file with one of the specified extensions beneath the given path.
    /// </summary>
    public static void DeleteFilesWithExtension(string path, string[] extensionsToDelete, SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        foreach (string file in Directory.GetFiles(path, "*.*", searchOption))
        {
            if (extensionsToDelete.Contains(Path.GetExtension(file), StringComparer.InvariantCultureIgnoreCase))
            {
                DeleteFile(file);
            }
        }
    }

    /// <summary>
    /// Deletes every file whose extension is not included in the provided allowlist.
    /// </summary>
    public static void DeleteFilesWithoutExtension(string path, string[] extensionsToKeep, SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        foreach (string file in Directory.GetFiles(path, "*.*", searchOption))
        {
            if (!extensionsToKeep.Contains(Path.GetExtension(file), StringComparer.InvariantCultureIgnoreCase))
            {
                DeleteFile(file);
            }
        }
    }

    /// <summary>
    /// Recursively deletes one directory tree after clearing file attributes that would otherwise block deletion.
    /// </summary>
    public static void DeleteDirectory(string targetDirectory)
    {
        foreach (string file in Directory.GetFiles(targetDirectory))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }

        foreach (string directory in Directory.GetDirectories(targetDirectory))
        {
            DeleteDirectory(directory);
        }

        Directory.Delete(targetDirectory, true);
    }
}
