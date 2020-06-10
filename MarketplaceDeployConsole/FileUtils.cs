using System;
using System.IO;
using System.IO.Compression;

namespace MarketplaceDeployConsole
{
    class FileUtils
    {
        // The leaf SourcePath directory will be renamed to the leaf DestinationPath directory, not placed inside
        public static void CopyDirectory(string SourcePath, string DestinationPath)
        {
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

        public static void CopyFile(string SourcePath, string DestinationPath, string FileName, bool ErrorIfSourceMissing = true)
        {
            if (!File.Exists(SourcePath) && !ErrorIfSourceMissing)
            {
                // Missing source but want to quietly fail
                // If we do want an error then File.Copy will throw it
                return;
            }
            File.Copy(Path.Combine(SourcePath, FileName), Path.Combine(DestinationPath, FileName), true);
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
            ConsoleUtils.RetryLoop(() =>
            {
                File.Delete(FilePath);
            }, "Couldn't access file", true);
        }

        public static void DeleteDirectory(string target_dir)
        {
            if (target_dir.Length < 2 || !target_dir.StartsWith(@"\\"))
            {
                target_dir = @"\\?\" + target_dir;
            }

            string[] files = { };

            ConsoleUtils.RetryLoop(() =>
            {
                files = Directory.GetFiles(target_dir);
            }, "Couldn't access directory", true);

            string[] dirs = Directory.GetDirectories(target_dir);

            foreach (string file in files)
            {
                File.SetAttributes(file, FileAttributes.Normal);
                //Console.WriteLine("Deleting file: " + file);

                ConsoleUtils.RetryLoop(() =>
                {
                    File.Delete(file);
                }, "Couldn't delete file", true);

            }

            foreach (string dir in dirs)
            {
                DeleteDirectory(dir);
            }

            //Console.WriteLine("Deleting directory: " + target_dir);

            ConsoleUtils.RetryLoop(() =>
            {
                Directory.Delete(target_dir, true);
            }, "Couldn't delete directory", true);

        }

        public static void CreateZipFromDirectory(string SourceDirectory, string DestinationArchiveFileName, bool IncludeBaseDirectory)
        {
            ConsoleUtils.RetryLoop(() =>
            {
                ZipFile.CreateFromDirectory(SourceDirectory, DestinationArchiveFileName, CompressionLevel.Optimal, IncludeBaseDirectory);
            }, "Couldn't create zip", true);

        }
    }
}
