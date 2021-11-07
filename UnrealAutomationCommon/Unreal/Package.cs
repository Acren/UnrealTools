﻿using Newtonsoft.Json;
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
            if (PackagePaths.Instance.IsTargetFile(path))
                ExecutablePath = path;
            else if (PackagePaths.Instance.IsTargetDirectory(path))
                ExecutablePath = PackagePaths.Instance.FindTargetFile(path);
            else
                ExecutablePath = path;

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

        private EngineInstallVersion EngineVersion => IsValid ? new EngineInstallVersion(FileVersionInfo.GetVersionInfo(ExecutablePath)) : null;

        public EngineInstall EngineInstall => EngineVersion != null ? EngineInstallFinder.GetEngineInstall(EngineVersion) : null;

        public string EngineInstallName => EngineInstall != null ? EngineInstall.DisplayName : EngineVersion?.ToString();

        public Package ProvidedPackage => this;

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