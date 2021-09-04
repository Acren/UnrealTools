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
        [JsonConstructor]
        public Package(string executablePath)
        {
            if (string.IsNullOrEmpty(executablePath))
            {
                throw new Exception("Invalid path");
            }

            if (IsPackageFile(executablePath))
            {
                ExecutablePath = executablePath;
            }
            else if(IsPackageDirectory(executablePath))
            {
                ExecutablePath = FindExecutablePath(executablePath);
            }
            else
            {
                throw new Exception($"Path '{executablePath}' does not appear to be a package (contains no executable)");
            }

            Name = Path.GetFileNameWithoutExtension(ExecutablePath);
        }

        public Package ProvidedPackage => this;

        public override string TargetPath => ExecutablePath;
        public string ExecutablePath { get; private set; }
        public override string Name { get; }

        public override void LoadDescriptor()
        {
            throw new NotImplementedException();
        }

        private static string FindExecutablePath(string path)
        {
            if(!Directory.Exists(path))
            {
                return null;
            }

            string[] files = Directory.GetFiles(path);
            foreach (string file in files)
            {
                if (System.IO.Path.GetExtension(file) == ".exe")
                {
                    return file;
                }
            }

            return null;
        }

        public string LogsPath => System.IO.Path.Combine(TargetDirectory, Name, "Saved", "Logs");

        private EngineInstallVersion EngineVersion => new(FileVersionInfo.GetVersionInfo(ExecutablePath));

        public EngineInstall EngineInstall => EngineInstallFinder.GetEngineInstall(EngineVersion);

        public string EngineInstallName => EngineInstall != null ? EngineInstall.DisplayName : EngineVersion.ToString();

        public static bool IsPackageFile(string path)
        {
            return FileUtils.HasExtension(path, ".exe");
        }

        public static bool IsPackageDirectory(string path)
        {
            return FindExecutablePath(path) != null;
        }
    }
}
