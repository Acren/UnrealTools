using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace UnrealAutomationCommon
{
    internal sealed class FileMaterializationEntry
    {
        public FileMaterializationEntry(string relativePath, bool required = false)
        {
            RelativePath = relativePath ?? throw new ArgumentNullException(nameof(relativePath));
            Required = required;
        }

        public string RelativePath { get; }

        public bool Required { get; }
    }

    internal sealed class FileMaterializationSpec : IEnumerable<FileMaterializationEntry>
    {
        public List<FileMaterializationEntry> Entries { get; } = new();

        public void Add(string relativePath, bool required = false)
        {
            Entries.Add(new FileMaterializationEntry(relativePath, required));
        }

        public IEnumerator<FileMaterializationEntry> GetEnumerator() => Entries.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal class FileUtils
    {
        // placeInside: SourcePath directory will be copied inside DestinationPath, otherwise the copy will be renamed to DestinationPath
        public static void CopyDirectory(string SourcePath, string DestinationPath, bool placeInside = false)
        {
            SourcePath = Path.GetFullPath(SourcePath);
            DestinationPath = Path.GetFullPath(DestinationPath);

            if (placeInside)
            {
                string dirName = new DirectoryInfo(SourcePath).Name;
                DestinationPath = Path.Combine(DestinationPath, dirName);
            }

            Directory.CreateDirectory(DestinationPath);

            //Now Create all of the directories
            foreach (string dirPath in Directory.GetDirectories(SourcePath, "*", SearchOption.AllDirectories))
            {
                string relativeDirectoryPath = Path.GetRelativePath(SourcePath, dirPath);
                Directory.CreateDirectory(Path.Combine(DestinationPath, relativeDirectoryPath));
            }

            //Copy all the files & Replaces any files with the same name
            foreach (string newPath in Directory.GetFiles(SourcePath, "*.*", SearchOption.AllDirectories))
            {
                string relativeFilePath = Path.GetRelativePath(SourcePath, newPath);
                string destinationFilePath = Path.Combine(DestinationPath, relativeFilePath);
                string? destinationDirectoryPath = Path.GetDirectoryName(destinationFilePath);
                if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
                {
                    Directory.CreateDirectory(destinationDirectoryPath);
                }

                File.Copy(newPath, destinationFilePath, true);
            }
        }

        public static void CopySubdirectory(string SourcePath, string DestinationPath, string Subdirectory)
        {
            Directory.CreateDirectory(Path.Combine(DestinationPath, Subdirectory));
            CopyDirectory(Path.Combine(SourcePath, Subdirectory), Path.Combine(DestinationPath, Subdirectory));
        }

        public static void MaterializeDirectory(string sourceRootPath, string destinationRootPath, FileMaterializationSpec spec)
        {
            if (spec == null)
            {
                throw new ArgumentNullException(nameof(spec));
            }

            sourceRootPath = Path.GetFullPath(sourceRootPath);
            destinationRootPath = Path.GetFullPath(destinationRootPath);
            Directory.CreateDirectory(destinationRootPath);

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

        public static void CopyFile(string SourceDirectoryPath, string DestinationDirectoryPath, string FileName, bool ErrorIfSourceMissing = true, bool Overwrite = false)
        {
            CopyFile(Path.Combine(SourceDirectoryPath, FileName), DestinationDirectoryPath, ErrorIfSourceMissing, Overwrite);
        }

        public static void CopyFile(string SourceFilePath, string DestinationDirectoryPath, bool ErrorIfSourceMissing = true, bool Overwrite = false)
        {
            if (!File.Exists(SourceFilePath) && !ErrorIfSourceMissing)
            {
                // Missing source but want to quietly fail
                // If we do want an error then File.Copy will throw it
                return;
            }

            string fileName = Path.GetFileName(SourceFilePath);
            string destinationFilePath = Path.Combine(DestinationDirectoryPath, fileName);
            if (Overwrite)
            {
                DeleteFileIfExists(destinationFilePath);
            }

            File.Copy(SourceFilePath, destinationFilePath);
        }

        public static void DeleteDirectoryIfExists(string Path)
        {
            if (Directory.Exists(Path))
            {
                DeleteDirectory(Path);
            }
        }

        public static void DeleteOtherSubdirectories(string TopPath, string[] SubdirectoriesToKeep)
        {
            foreach (string SubDirectory in Directory.GetDirectories(TopPath, "*", SearchOption.TopDirectoryOnly))
            {
                if (Array.IndexOf(SubdirectoriesToKeep, Path.GetFileName(SubDirectory)) < 0)
                {
                    // Not allowed, delete
                    DeleteDirectoryIfExists(SubDirectory);
                }
            }
        }

        public static void DeleteFile(string FilePath)
        {
            File.Delete(FilePath);
        }

        public static void DeleteFileIfExists(string FilePath)
        {
            if (File.Exists(FilePath))
            {
                DeleteFile(FilePath);
            }
        }

        public static void DeleteFilesWithExtension(string path, string[] extensionsToDelete, SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            string[] files = Directory.GetFiles(path, "*.*", searchOption);

            foreach (string file in files)
            {
                if (extensionsToDelete.Contains(Path.GetExtension(file), StringComparer.InvariantCultureIgnoreCase))
                {
                    DeleteFile(file);
                }
            }
        }

        public static void DeleteFilesWithoutExtension(string path, string[] extensionsToKeep, SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            string[] files = Directory.GetFiles(path, "*.*", searchOption);

            foreach (string file in files)
            {
                if (!extensionsToKeep.Contains(Path.GetExtension(file), StringComparer.InvariantCultureIgnoreCase))
                {
                    DeleteFile(file);
                }
            }
        }

        public static void DeleteDirectory(string target_dir)
        {
            string[] files = { };

            files = Directory.GetFiles(target_dir);

            string[] dirs = Directory.GetDirectories(target_dir);

            foreach (string file in files)
            {
                File.SetAttributes(file, FileAttributes.Normal);

                File.Delete(file);
            }

            foreach (string dir in dirs)
            {
                DeleteDirectory(dir);
            }

            Directory.Delete(target_dir, true);
        }

        public static void CreateZipFromDirectory(string SourceDirectory, string DestinationArchiveFileName, bool IncludeBaseDirectory)
        {
            ZipFile.CreateFromDirectory(SourceDirectory, DestinationArchiveFileName, CompressionLevel.Optimal,
                IncludeBaseDirectory);
        }

        // Crude blocking way
        public static void WaitForFileReadable(string filePath)
        {
            var secondsWaited = 0;
            while (true)
            {
                try
                {
                    using (StreamReader stream = new(filePath))
                    {
                        break;
                    }
                }
                catch
                {
                    Thread.Sleep(1000);
                    secondsWaited++;
                }

                if (secondsWaited >= 10)
                {
                    throw new Exception("Timed out");
                }
            }
        }

        public static bool HasExtension(string filePath, string extension)
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            if (!Path.HasExtension(filePath))
            {
                return false;
            }

            return Path.GetExtension(filePath).Equals(extension, StringComparison.InvariantCultureIgnoreCase);
        }
    }
}
