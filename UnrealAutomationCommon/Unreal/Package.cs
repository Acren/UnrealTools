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
        public Package(string executablePath)
        {
            ExecutablePath = executablePath;
            if (!IsPackage(TargetDirectory))
            {
                throw new Exception($"Path '{executablePath}' does not appear to be a package (contains no executable)");
            }

            Name = System.IO.Path.GetFileNameWithoutExtension(ExecutablePath);
        }

        public Package ProvidedPackage => this;

        public override string TargetPath => ExecutablePath;
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

        public string LogsPath => System.IO.Path.Combine(TargetDirectory, Name, "Saved", "Logs");

        public EngineInstall EngineInstall {
            get
            {
                EngineInstallVersion version = new EngineInstallVersion(FileVersionInfo.GetVersionInfo(ExecutablePath));
                return EngineInstallFinder.GetEngineInstall(version);
            }
        }
    }
}
