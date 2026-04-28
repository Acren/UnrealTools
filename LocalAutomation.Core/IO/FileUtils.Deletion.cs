using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

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
        DeleteTopLevelDirectoriesExcept(topPath, subdirectoriesToKeep);
    }

    /// <summary>
    /// Deletes relative child directories under a root path when they exist.
    /// </summary>
    public static void DeleteRelativeDirectories(string rootPath, IEnumerable<string> relativeDirectories, ILogger? logger = null)
    {
        _ = relativeDirectories ?? throw new ArgumentNullException(nameof(relativeDirectories));

        foreach (string relativeDirectory in relativeDirectories)
        {
            DeleteDirectoryIfExists(Path.Combine(rootPath, relativeDirectory), logger);
        }
    }

    /// <summary>
    /// Deletes top-level files under a directory for each supplied search pattern and logs each deletion.
    /// </summary>
    public static void DeleteTopLevelFiles(string path, IEnumerable<string> filePatterns, ILogger? logger = null)
    {
        _ = filePatterns ?? throw new ArgumentNullException(nameof(filePatterns));

        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (string filePattern in filePatterns)
        {
            foreach (string filePath in Directory.GetFiles(path, filePattern, SearchOption.TopDirectoryOnly))
            {
                logger?.LogInformation("Deleting file: {FilePath}", filePath);
                DeleteFileIfExists(filePath);
            }
        }
    }

    /// <summary>
    /// Deletes every immediate child directory except the explicitly preserved names.
    /// </summary>
    public static void DeleteTopLevelDirectoriesExcept(string path, IEnumerable<string> preservedDirectoryNames)
    {
        _ = preservedDirectoryNames ?? throw new ArgumentNullException(nameof(preservedDirectoryNames));

        if (!Directory.Exists(path))
        {
            return;
        }

        HashSet<string> preservedNames = preservedDirectoryNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string directoryPath in Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly))
        {
            if (preservedNames.Contains(Path.GetFileName(directoryPath)))
            {
                continue;
            }

            DeleteDirectoryIfExists(directoryPath);
        }
    }

    /// <summary>
    /// Deletes every immediate child directory except the explicitly preserved names and logs each deletion.
    /// </summary>
    public static void DeleteTopLevelDirectoriesExcept(string path, IEnumerable<string> preservedDirectoryNames, ILogger? logger = null)
    {
        _ = preservedDirectoryNames ?? throw new ArgumentNullException(nameof(preservedDirectoryNames));

        if (!Directory.Exists(path))
        {
            return;
        }

        HashSet<string> preservedNames = preservedDirectoryNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string directoryPath in Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly))
        {
            if (preservedNames.Contains(Path.GetFileName(directoryPath)))
            {
                continue;
            }

            DeleteDirectoryIfExists(directoryPath, logger);
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
    /// Deletes one directory tree only when it exists and logs the deletion.
    /// </summary>
    public static void DeleteDirectoryIfExists(string path, ILogger? logger = null)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        logger?.LogInformation("Deleting directory: {DirectoryPath}", path);
        DeleteDirectoryIfExists(path);
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
