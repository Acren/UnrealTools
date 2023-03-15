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

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class VerifyDeployment : Operation<Plugin>
    {
        protected override async Task<OperationResult> OnExecuted(CancellationToken token)
        {
            Plugin plugin = GetTarget(OperationParameters);

            Logger.Log($"Versions: {string.Join(", ", OperationParameters.RequestOptions<EngineVersionOptions>().EnabledVersions.Value.Select(x => x.MajorMinorString)) }");
            foreach (EngineInstallVersion engineVersion in OperationParameters.RequestOptions<EngineVersionOptions>().EnabledVersions.Value)
            {
                EngineInstall engineInstall = EngineInstallFinder.GetEngineInstall(engineVersion);
                if (engineInstall == null)
                {
                    throw new Exception("Engine not found");
                }
                OperationResult result = await VerifyForEngine(engineInstall, token);
                if (!result.Success)
                {
                    // Failure
                    return result;
                }
            }

            return new OperationResult(true);
        }

        private async Task<OperationResult> VerifyForEngine(EngineInstall engine, CancellationToken token)
        {
            Plugin plugin = GetTarget(OperationParameters);
            EngineInstallVersion engineVersion = engine.Version;
            Logger.Log($"Verifying plugin {plugin.Name} for {engineVersion.MajorMinorString}");

            Plugin installedPlugin = engine.FindInstalledPlugin(plugin.Name);

            if (installedPlugin is null)
            {
                throw new Exception($"Could not find plugin {plugin.Name} in engine located at {engine.InstallDirectory}");
            }

            string pluginVersionName = plugin.PluginDescriptor.VersionName;
            Logger.Log($"Source plugin version: {pluginVersionName}");
            string installedPluginVersionName = installedPlugin.PluginDescriptor.VersionName;
            Logger.Log($"Installed plugin version: {pluginVersionName}");

            if (!installedPluginVersionName.Contains(pluginVersionName))
            {
                throw new Exception($"Installed plugin version {installedPluginVersionName} does not include reference version {pluginVersionName}");
            }

            string exampleProjects = OperationParameters.FindOptions<VerifyDeploymentOptions>().ExampleProjectsPath;
            string exampleProjectZip = FindExampleProjectZip(plugin, exampleProjects, engine);
            if (exampleProjectZip == null)
            {
                throw new Exception($"Could not find example project zip in {exampleProjects}");
            }

            Logger.Log($"Identified {exampleProjectZip} as best example project");

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

            AutomationOptions automationOpts = OperationParameters.FindOptions<AutomationOptions>();
            automationOpts.TestNameOverride = plugin.TestName;

            if (automationOpts.RunTests)
            {
                Logger.Log("Launching and testing example project editor");

                OperationParameters launchEditorParams = new()
                {
                    Target = exampleProject,
                    EngineOverride = engine
                };
                launchEditorParams.SetOptions(automationOpts);

                if (!(await new LaunchEditor().Execute(launchEditorParams, Logger, token)).Success)
                {
                    throw new Exception("Failed to launch example project");
                }
            }

            // Launch and test standalone

            if (automationOpts.RunTests)
            {
                Logger.Log("Launching and testing standalone");

                OperationParameters launchStandaloneParams = new()
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
            OperationParameters packageExampleProjectParams = new()
            {
                Target = exampleProject,
                EngineOverride = engine,
                OutputPathOverride = packageOutput
            };

            OperationResult packageExampleProjectResult = await packageExampleProject.Execute(packageExampleProjectParams, Logger, token);

            if (!packageExampleProjectResult.Success)
            {
                throw new Exception("Failed to package example project");
            }

            // Test the package

            if (automationOpts.RunTests)
            {
                OperationParameters testPackageParams = new()
                {
                    Target = exampleProject,
                    EngineOverride = engine
                };
                testPackageParams.SetOptions(automationOpts);

                OperationResult testPackageResult = await new LaunchStagedPackage().Execute(testPackageParams, Logger, token);

                if (!testPackageResult.Success)
                {
                    throw new Exception("Launch and test package failed");
                }
            }

            Logger.Log($"Finished verifying plugin {plugin.Name} for {engineVersion.MajorMinorString}");

            return new OperationResult(true);
        }

        protected override IEnumerable<Command> BuildCommands(OperationParameters operationParameters)
        {
            operationParameters.RequestOptions<EngineVersionOptions>();
            operationParameters.RequestOptions<AutomationOptions>();
            operationParameters.RequestOptions<VerifyDeploymentOptions>();
            return new List<Command>();
        }

        private string FindExampleProjectZip(Plugin plugin, string exampleProjectsPath, EngineInstall engine)
        {
            string pluginVersionName = plugin.PluginDescriptor.VersionName;
            string exampleProjects = exampleProjectsPath;
            string extension = "*.zip";
            string[] zipPaths = Directory.GetFiles(exampleProjects, extension, SearchOption.AllDirectories);
            List<string> validZipPaths = new();

            foreach (string zipPath in zipPaths)
            {
                string zipName = Path.GetFileNameWithoutExtension(zipPath);
                if (!zipName.Contains(plugin.Name))
                {
                    continue;
                }

                if (!zipName.Contains("ExampleProject"))
                {
                    continue;
                }

                validZipPaths.Add(zipPath);
            }

            if (validZipPaths.Count == 0)
            {
                throw new Exception("No valid zips");
            }

            if (validZipPaths.Count == 1)
            {
                return validZipPaths[0];
            }

            List<string> zipPathsWithExactVersion = new();
            foreach (string zipName in validZipPaths)
            {
                if (!zipName.Contains(pluginVersionName))
                {
                    continue;
                }
                zipPathsWithExactVersion.Add(zipName);
            }

            if (zipPathsWithExactVersion.Count == 1)
            {
                return zipPathsWithExactVersion[0];
            }

            List<string> candidateZips;
            if (zipPathsWithExactVersion.Count == 0)
            {
                candidateZips = validZipPaths;
            }
            else
            {
                candidateZips = zipPathsWithExactVersion;
            }

            foreach (string zipName in candidateZips)
            {
                if (zipName.Contains(engine.Version.MajorMinorString))
                {
                    return zipName;
                }
            }

            return candidateZips[0];
        }
    }
}
