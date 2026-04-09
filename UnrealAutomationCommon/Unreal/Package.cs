using System;
using System.Diagnostics;
using System.IO;
using LocalAutomation.Extensions.Abstractions;
using LocalAutomation.Runtime;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    public interface IPackageProvider : IOperationTarget
    {
        public Package? GetProvidedPackage(Engine engineContext);
    }

    [Target]
    public class Package : OperationTarget, IPackageProvider, IEngineInstanceProvider
    {
        [JsonConstructor]
        public Package(string targetPath)
        {
            if (!PackagePaths.Instance.IsTargetDirectory(targetPath))
            {
                AppLogger.LoggerInstance.LogError($"Package {targetPath} does not contain executable");
                return;
            }

            TargetPath = targetPath;
            Name = Path.GetFileNameWithoutExtension(ExecutablePath);
        }

        public string ExecutablePath => PackagePaths.Instance.FindRequiredTargetFile(TargetPath);

        public Project? HostProject
        {
            get
            {
                string projectPath = Path.GetFullPath(Path.Combine(TargetDirectory, @"..\..\..\")); // Up 3 levels
                if (ProjectPaths.Instance.IsTargetDirectory(projectPath))
                {
                    return new Project(projectPath);
                }

                return null;
            }
        }

        public string LogsPath => Path.Combine(TargetDirectory, Name, "Saved", "Logs");

        private EngineVersion EngineVersion => new(FileVersionInfo.GetVersionInfo(ExecutablePath));

        public Engine EngineInstance => EngineFinder.GetRequiredEngineInstall(EngineVersion);

        public string EngineInstanceName => EngineInstance.DisplayName;

        public Package GetProvidedPackage(Engine engineContext) => this;

        public override string Name { get; } = string.Empty;

        public override IOperationTarget? ParentTarget => HostProject;

        public override bool IsValid => PackagePaths.Instance.IsTargetFile(ExecutablePath);

        public override void LoadDescriptor()
        {
            throw new NotImplementedException();
        }
    }
}
