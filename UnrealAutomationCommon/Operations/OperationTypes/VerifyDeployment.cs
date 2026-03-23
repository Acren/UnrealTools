using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;
using Microsoft.Extensions.Logging;
using Semver;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class VerifyDeployment : UnrealOperation<Plugin>
    {
        protected override async Task<global::LocalAutomation.Runtime.OperationResult> OnExecuted(CancellationToken token)
        {
            Plugin plugin = GetTarget(UnrealOperationParameters);

            Logger.LogInformation($"Versions: {string.Join(", ", UnrealOperationParameters.RequestOptions<EngineVersionOptions>().EnabledVersions.Value.Select(x => x.MajorMinorString)) }");
            foreach (EngineVersion engineVersion in UnrealOperationParameters.RequestOptions<EngineVersionOptions>().EnabledVersions.Value)
            {
                Engine engine = EngineFinder.GetEngineInstall(engineVersion);
                if (engine == null)
                {
                    throw new Exception("Engine not found");
                }
                global::LocalAutomation.Runtime.OperationResult result = await VerifyForEngine(engine, token);
                if (!result.Success)
                {
                    // Failure
                    return result;
                }
            }

            return new global::LocalAutomation.Runtime.OperationResult(true);
        }

        /// <summary>
        /// Deployment verification always needs engine-version selection, automation toggles, and example-project
        /// settings to know which builds to verify and how deeply to test them.
        /// </summary>
        protected override void CollectRequiredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target, System.Collections.Generic.ISet<System.Type> optionSetTypes)
        {
            base.CollectRequiredOptionSetTypes(target, optionSetTypes);
            optionSetTypes.Add(typeof(EngineVersionOptions));
            optionSetTypes.Add(typeof(AutomationOptions));
            optionSetTypes.Add(typeof(VerifyDeploymentOptions));
        }

        private async Task<global::LocalAutomation.Runtime.OperationResult> VerifyForEngine(Engine engine, CancellationToken token)
        {
            Plugin plugin = GetTarget(UnrealOperationParameters);
            EngineVersion engineVersion = engine.Version;
            Logger.LogInformation($"Verifying plugin {plugin.Name} for {engineVersion.MajorMinorString}");

            Plugin installedPlugin = engine.FindInstalledPlugin(plugin.Name);

            if (installedPlugin is null)
            {
                throw new Exception($"Could not find plugin {plugin.Name} in engine located at {engine.TargetPath}");
            }

            string pluginVersionName = plugin.PluginDescriptor.VersionName;
            Logger.LogInformation($"Source plugin version: {pluginVersionName}");
            string installedPluginVersionName = installedPlugin.PluginDescriptor.VersionName;
            Logger.LogInformation($"Installed plugin version: {pluginVersionName}");

            if (!installedPluginVersionName.Contains(pluginVersionName))
            {
                throw new Exception($"Installed plugin version {installedPluginVersionName} does not include reference version {pluginVersionName}");
            }

            string exampleProjects = UnrealOperationParameters.FindOptions<VerifyDeploymentOptions>().ExampleProjectsPath;
            string exampleProjectZip = FindExampleProjectZip(plugin, exampleProjects, engine);
            if (exampleProjectZip == null)
            {
                throw new Exception($"Could not find example project zip in {exampleProjects}");
            }

            Logger.LogInformation($"Identified {exampleProjectZip} as best example project");

            string temp = GetOperationTempPath();
            string exampleProjectTestPath = Path.Combine(temp, "ExampleProject");

            FileUtils.DeleteDirectoryIfExists(exampleProjectTestPath);
            ZipFile.ExtractToDirectory(exampleProjectZip, exampleProjectTestPath);

            Project exampleProject = new Project(exampleProjectTestPath);

            if (exampleProject == null)
            {
                throw new Exception($"Couldn't create project from {exampleProjectZip}");
            }

            // Launch and test example project editor

            AutomationOptions automationOpts = UnrealOperationParameters.FindOptions<AutomationOptions>();

            if (automationOpts.RunTests)
            {
                Logger.LogInformation("Launching and testing example project editor");

                UnrealOperationParameters launchEditorParams = new()
                {
                    Target = exampleProject,
                    EngineOverride = engine
                };
                launchEditorParams.SetOptions(automationOpts);

                if (!(await new LaunchProjectEditor().Execute(launchEditorParams, Logger, token)).Success)
                {
                    throw new Exception("Failed to launch example project");
                }
            }

            // Launch and test standalone

            if (automationOpts.RunTests)
            {
                Logger.LogInformation("Launching and testing standalone");

                UnrealOperationParameters launchStandaloneParams = new()
                {
                    Target = exampleProject,
                    EngineOverride = engine
                };
                launchStandaloneParams.SetOptions(automationOpts);

                if (!(await new LaunchStandalone().Execute(launchStandaloneParams, Logger, token)).Success)
                {
                    throw new Exception("Failed to launch standalone");
                }
            }

            // Package example project and test

            string packageOutput = Path.Combine(temp, "Package");

            FileUtils.DeleteDirectoryIfExists(packageOutput);

            PackageProject packageExampleProject = new();
            UnrealOperationParameters packageExampleProjectParams = new()
            {
                Target = exampleProject,
                EngineOverride = engine,
                OutputPathOverride = packageOutput
            };

            global::LocalAutomation.Runtime.OperationResult packageExampleProjectResult = await packageExampleProject.Execute(packageExampleProjectParams, Logger, token);

            if (!packageExampleProjectResult.Success)
            {
                throw new Exception("Failed to package example project");
            }

            // Test the package

            if (automationOpts.RunTests)
            {
                UnrealOperationParameters testPackageParams = new()
                {
                    Target = exampleProject,
                    EngineOverride = engine
                };
                testPackageParams.SetOptions(automationOpts);

                global::LocalAutomation.Runtime.OperationResult testPackageResult = await new LaunchStagedPackage().Execute(testPackageParams, Logger, token);

                if (!testPackageResult.Success)
                {
                    throw new Exception("Launch and test package failed");
                }
            }

            Logger.LogInformation($"Finished verifying plugin {plugin.Name} for {engineVersion.MajorMinorString}");

            return new global::LocalAutomation.Runtime.OperationResult(true);
        }

        protected override IEnumerable<LocalAutomation.Runtime.Command> BuildCommands(global::LocalAutomation.Runtime.OperationParameters operationParameters)
        {
            return new List<LocalAutomation.Runtime.Command>();
        }

        public readonly struct ExampleProjectZipInfo
        {
            public readonly string Path;
            public readonly string PluginName;
            public readonly SemVersion PluginVersion;
            public readonly SemVersion EngineVersion;
            public readonly bool IsExampleProject;

            public ExampleProjectZipInfo(string path)
            {
                Path = path;
                string zipName = System.IO.Path.GetFileNameWithoutExtension(path);

                // Expect zip to be named PluginName_PluginVersion_EngineVersion_ExampleProject.zip
                string[] split = zipName.Split('_');
                IsExampleProject = split.Length > 3 && split[3] == "ExampleProject";
                if (!IsExampleProject)
                {
                    return;
                }
                PluginName = split[0];
                PluginVersion = split.Length > 1 ? SemVersion.Parse(split[1], SemVersionStyles.Any) : null;
                EngineVersion = split.Length > 2 ? SemVersion.Parse(split[2].Replace("UE",""), SemVersionStyles.Any) : null;
            }
        }

        private string FindExampleProjectZip(Plugin plugin, string exampleProjectsPath, Engine engine)
        {
            SemVersion pluginVersion = plugin.PluginDescriptor.SemVersion;
            string exampleProjects = exampleProjectsPath;
            string extension = "*.zip";
            string[] zipPaths = Directory.GetFiles(exampleProjects, extension, SearchOption.AllDirectories);
            List<ExampleProjectZipInfo> pluginExampleProjectZips = new();

            foreach (string zipPath in zipPaths)
            {
                ExampleProjectZipInfo zipInfo = new (zipPath);
                if (zipInfo.IsExampleProject && zipInfo.PluginName == plugin.Name)
                {
                    pluginExampleProjectZips.Add(zipInfo);
                }
            }

            if (pluginExampleProjectZips.Count == 0)
            {
                throw new Exception("No valid zips");
            }

            ExampleProjectZipInfo selectedZip = pluginExampleProjectZips
                .Where(z => z.PluginVersion <= pluginVersion && z.EngineVersion <= engine.SemVersion)
                .OrderByDescending(z => z.PluginVersion)
                .ThenByDescending(z => z.EngineVersion)
                .First();

            return selectedZip.Path;
        }
    }
}
