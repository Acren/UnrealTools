using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    public interface IPackageProvider : IOperationTarget
    {
        [JsonIgnore]
        public Package ProvidedPackage { get; }
    }

    public class Package : OperationTarget, IPackageProvider, IEngineInstallProvider
    {
        public Package(string packagePath)
        {
            if (!IsPackage(packagePath))
            {
                throw new Exception($"Path '{packagePath}' does not appear to be a package (contains no executable)");
            }

            PackagePath = packagePath;

            ExecutablePath = FindExecutablePath(PackagePath);
            Name = System.IO.Path.GetFileNameWithoutExtension(ExecutablePath);
        }

        public Package ProvidedPackage => this;

        public string PackagePath { get; private set; }
        public override string TargetPath => PackagePath;
        public string ExecutablePath { get; private set; }
        public override string Name { get;}

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

        public string LogsPath => System.IO.Path.Combine(PackagePath, Name, "Saved", "Logs");

        public EngineInstall EngineInstall {
            get
            {
                EngineInstallVersion version = new EngineInstallVersion(FileVersionInfo.GetVersionInfo(ExecutablePath));
                return EngineInstallFinder.GetEngineInstall(version);
            }
        }
    }
}
