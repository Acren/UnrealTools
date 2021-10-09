using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    public interface IPackageProvider : IOperationTarget
    {
        public Package ProvidedPackage { get; }
    }

    public class Package : OperationTarget, IPackageProvider, IEngineInstallProvider
    {
        [JsonConstructor]
        public Package([JsonProperty("ExecutablePath")] string path)
        {
            if (IsPackageFile(path))
            {
                ExecutablePath = path;
            }
            else if (IsPackageDirectory(path))
            {
                ExecutablePath = FindExecutablePath(path);
            }
            else
            {
                ExecutablePath = path;
            }

            Name = Path.GetFileNameWithoutExtension(ExecutablePath);
        }

        public Package ProvidedPackage => this;

        public override string TargetPath => ExecutablePath;

        [JsonProperty]
        public string ExecutablePath { get; private set; }
        public override string Name { get; }

        public override bool IsValid => IsPackageFile(ExecutablePath);

        public override void LoadDescriptor()
        {
            throw new NotImplementedException();
        }

        private static string FindExecutablePath(string path)
        {
            if (!Directory.Exists(path))
            {
                return null;
            }

            string[] files = Directory.GetFiles(path);
            foreach (string file in files)
            {
                if (System.IO.Path.GetExtension(file).Equals(".exe", StringComparison.InvariantCultureIgnoreCase))
                {
                    return file;
                }
            }

            return null;
        }

        public string LogsPath => System.IO.Path.Combine(TargetDirectory, Name, "Saved", "Logs");

        private EngineInstallVersion EngineVersion => IsValid ? new(FileVersionInfo.GetVersionInfo(ExecutablePath)) : null;

        public EngineInstall EngineInstall => EngineVersion != null ? EngineInstallFinder.GetEngineInstall(EngineVersion) : null;

        public string EngineInstallName => EngineInstall != null ? EngineInstall.DisplayName : EngineVersion?.ToString();

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
