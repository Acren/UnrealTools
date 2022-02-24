using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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

            foreach (EngineInstallVersion engineVersion in plugin.TargetEngineVersions)
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
            string installedPluginVersionName = installedPlugin.PluginDescriptor.VersionName;

            if (pluginVersionName != installedPluginVersionName)
            {
                throw new Exception($"Installed plugin version {installedPluginVersionName} does not match reference version {pluginVersionName}");
            }

            string exampleProjects = @"J:\My Drive\MarketplacePublic"; // todo expose path option
            string extension = "*.zip";
            string[] zipPaths = Directory.GetFiles(exampleProjects, extension, SearchOption.AllDirectories);

            string exampleProjectZip = null; 
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

                if (!zipName.Contains(pluginVersionName))
                {
                    continue;
                }

                exampleProjectZip = zipPath;
                break;
            }

            if (exampleProjectZip == null)
            {
                throw new Exception($"Could not find example project zip in {exampleProjects}");
            }

            string temp = GetOperationTempPath();
            string exampleProjectTestPath = Path.Combine(temp, "ExampleProject");
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
            return new List<Command>();
        }
    }
}
