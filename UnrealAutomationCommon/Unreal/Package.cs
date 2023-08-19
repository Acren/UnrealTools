using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    public interface IPackageProvider : IOperationTarget
    {
        public Package GetProvidedPackage(Engine engineContext);
    }

    public class Package : OperationTarget, IPackageProvider, IEngineInstanceProvider
    {
        [JsonConstructor]
        public Package([JsonProperty("ExecutablePath")] string path)
        {
            if (PackagePaths.Instance.IsTargetFile(path))
            {
                ExecutablePath = path;
            }
            else if (PackagePaths.Instance.IsTargetDirectory(path))
            {
                ExecutablePath = PackagePaths.Instance.FindTargetFile(path);
            }
            else
            {
                ExecutablePath = path;
            }

            Name = Path.GetFileNameWithoutExtension(ExecutablePath);
        }

        [JsonProperty] public string ExecutablePath { get; private set; }

        public Project HostProject
        {
            get
            {
                string projectPath = Path.GetFullPath(Path.Combine(TargetDirectory, @"..\..\..\")); // Up 3 levels
                if (ProjectPaths.Instance.IsTargetDirectory(projectPath))
                {
                    return new Project(ProjectPaths.Instance.FindTargetFile(projectPath));
                }

                return null;
            }
        }

        public string LogsPath => Path.Combine(TargetDirectory, Name, "Saved", "Logs");

        private EngineVersion EngineVersion => IsValid ? new EngineVersion(FileVersionInfo.GetVersionInfo(ExecutablePath)) : null;

        public Engine EngineInstance => EngineVersion != null ? EngineFinder.GetEngineInstall(EngineVersion) : null;

        public string EngineInstanceName => EngineInstance != null ? EngineInstance.DisplayName : EngineVersion?.ToString();

        public Package GetProvidedPackage(Engine engineContext) => this;

        public override string TargetPath => ExecutablePath;
        public override string Name { get; }

        public override IOperationTarget ParentTarget => HostProject;

        public override bool IsValid => PackagePaths.Instance.IsTargetFile(ExecutablePath);

        public override void LoadDescriptor()
        {
            throw new NotImplementedException();
        }
    }
}