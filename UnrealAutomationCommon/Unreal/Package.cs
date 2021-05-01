using System;
using System.IO;

namespace UnrealAutomationCommon.Unreal
{
    public class Package
    {
        public Package(string packagePath)
        {
            if (!IsPackage(packagePath))
            {
                throw new Exception("Path does not appear to be a package");
            }

            Path = packagePath;

            ExecutablePath = FindExecutablePath(Path);
            Name = System.IO.Path.GetFileNameWithoutExtension(ExecutablePath);
        }

        public string Path { get; private set; }
        public string ExecutablePath { get; private set; }
        public string Name { get; private set; }

        public static bool IsPackage(string path)
        {
            return FindExecutablePath(path) != null;
        }

        private static string FindExecutablePath(string path)
        {
            string[] files = Directory.GetFiles(path);
            foreach(string file in files)
            {
                if (System.IO.Path.GetExtension(file) == ".exe")
                {
                    return file;
                }
            }

            return null;
        }

        public string GetLogsPath()
        {
            return System.IO.Path.Combine(Path, Name, "Saved", "Logs");
        }
    }
}
