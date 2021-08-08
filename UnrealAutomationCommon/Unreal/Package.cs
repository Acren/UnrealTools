using System;
using System.IO;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    public interface IPackageSource
    {
        public Package ProvidedPackage { get; }
    }

    public class Package : OperationTarget, IPackageSource
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

        public Package ProvidedPackage => this;

        public string Path { get; private set; }
        public string ExecutablePath { get; private set; }
        public override string Name { get;}

        public override EngineInstall EngineInstall => null;

        public override void LoadDescriptor()
        {
            throw new NotImplementedException();
        }

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
