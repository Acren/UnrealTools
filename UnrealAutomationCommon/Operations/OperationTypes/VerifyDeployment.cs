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
            foreach (EngineVersion engineVersion in OperationParameters.RequestOptions<EngineVersionOptions>().EnabledVersions.Value)
            {
                Engine engine = EngineFinder.GetEngineInstall(engineVersion);
                if (engine == null)
                {
                    throw new Exception("Engine not found");
                }
                OperationResult result = await VerifyForEngine(engine, token);
                if (!result.Success)
                {
                    // Failure
                    return result;
                }
            }

            return new OperationResult(true);
        }

        private async Task<OperationResult> VerifyForEngine(Engine engine, CancellationToken token)
        {
            Plugin plugin = GetTarget(OperationParameters);
            EngineVersion engineVersion = engine.Version;
            Logger.Log($"Verifying plugin {plugin.Name} for {engineVersion.MajorMinorString}");

            Plugin installedPlugin = engine.FindInstalledPlugin(plugin.Name);

            if (installedPlugin is null)
            {
                throw new Exception($"Could not find plugin {plugin.Name} in engine located at {engine.TargetPath}");
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

                if (!(await new LaunchProjectEditor().Execute(launchEditorParams, Logger, token)).Success)
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

        public readonly struct ExampleProjectZipInfo
        {
            public readonly string Path;
            public readonly string PluginName;
            public readonly string PluginVersion;
            public readonly string EngineVersion;
            public readonly bool IsExampleProject;

            public ExampleProjectZipInfo(string path)
            {
                Path = path;
                string zipName = System.IO.Path.GetFileNameWithoutExtension(path);

                // Expect zip to be named PluginName_PluginVersion_EngineVersion_ExampleProject.zip
                string[] split = zipName.Split('_');
                PluginName = split[0];
                PluginVersion = split.Length > 1 ? split[1] : null;
                EngineVersion = split.Length > 2 ? split[2] : null;
                IsExampleProject = split.Length > 3 && split[3] == "ExampleProject";
            }
        }

        private string FindExampleProjectZip(Plugin plugin, string exampleProjectsPath, Engine engine)
        {
            string pluginVersionName = plugin.PluginDescriptor.VersionName;
            string exampleProjects = exampleProjectsPath;
            string extension = "*.zip";
            string[] zipPaths = Directory.GetFiles(exampleProjects, extension, SearchOption.AllDirectories);
            List<ExampleProjectZipInfo> validZips = new();

            foreach (string zipPath in zipPaths)
            {
                ExampleProjectZipInfo zipInfo = new (zipPath);
                if (zipInfo.IsExampleProject && zipInfo.PluginName == plugin.Name)
                {
                    validZips.Add(zipInfo);
                }
            }

            if (validZips.Count == 0)
            {
                throw new Exception("No valid zips");
            }

            if (validZips.Count == 1)
            {
                return validZips[0].Path;
            }

            List<ExampleProjectZipInfo> zipPathsWithExactVersion = new();
            foreach (ExampleProjectZipInfo zip in validZips)
            {
                if (zip.PluginVersion != pluginVersionName)
                {
                    continue;
                }
                zipPathsWithExactVersion.Add(zip);
            }

            if (zipPathsWithExactVersion.Count == 1)
            {
                return zipPathsWithExactVersion[0].Path;
            }

            List<ExampleProjectZipInfo> candidateZips;
            if (zipPathsWithExactVersion.Count == 0)
            {
                candidateZips = validZips;
            }
            else
            {
                candidateZips = zipPathsWithExactVersion;
            }

            foreach (ExampleProjectZipInfo zip in candidateZips)
            {
                if (zip.EngineVersion == engine.Version.MajorMinorString)
                {
                    return zip.Path;
                }
            }

            return candidateZips[0].Path;
        }
    }
}
