using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace UnrealAutomationCommon
{
    internal class FileUtils
    {
        // placeInside: SourcePath directory will be copied inside DestinationPath, otherwise the copy will be renamed to DestinationPath
        public static void CopyDirectory(string SourcePath, string DestinationPath, bool placeInside = false)
        {
            if (placeInside)
            {
                string dirName = new DirectoryInfo(SourcePath).Name;
                DestinationPath = Path.Combine(DestinationPath, dirName);
            }

            Directory.CreateDirectory(DestinationPath);

            //Now Create all of the directories
            foreach (string dirPath in Directory.GetDirectories(SourcePath, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dirPath.Replace(SourcePath, DestinationPath));

            //Copy all the files & Replaces any files with the same name
            foreach (string newPath in Directory.GetFiles(SourcePath, "*.*", SearchOption.AllDirectories))
                File.Copy(newPath, newPath.Replace(SourcePath, DestinationPath), true);
        }

        public static void CopySubdirectory(string SourcePath, string DestinationPath, string Subdirectory)
        {
            Directory.CreateDirectory(Path.Combine(DestinationPath, Subdirectory));
            CopyDirectory(Path.Combine(SourcePath, Subdirectory), Path.Combine(DestinationPath, Subdirectory));
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