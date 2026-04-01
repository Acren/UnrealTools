using Newtonsoft.Json;
using System;
using System.IO;
using Semver;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using LocalAutomation.Core;

namespace UnrealAutomationCommon.Unreal
{
    public class PluginDescriptor
    {
        public string VersionName { get; set; } = string.Empty;
        public string FriendlyName { get; set; } = string.Empty;
        public bool IsBetaVersion { get; set; }
        public string EngineVersionString { get; set; } = string.Empty;

        public List<ModuleDeclaration> Modules { get; set; } = new();

        public SemVersion SemVersion => SemVersion.Parse(VersionName, SemVersionStyles.Strict);
        public EngineVersion? EngineVersion => string.IsNullOrEmpty(EngineVersionString) ? null : new(EngineVersionString);

        public static PluginDescriptor Load(string uPluginPath)
        {
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("PluginDescriptor.Load")
                .SetTag("descriptor.path", uPluginPath);
            FileUtils.WaitForFileReadable(uPluginPath);
            try
            {
                return JsonConvert.DeserializeObject<PluginDescriptor>(File.ReadAllText(uPluginPath))
                    ?? throw new InvalidOperationException($"Could not deserialize plugin descriptor '{uPluginPath}'.");
            }
            catch (Exception ex)
            {
                try
                {
                    ApplicationLogger.Logger.LogError(ex, "Failed to deserialize plugin descriptor '{PluginDescriptorPath}'.", uPluginPath);
                }
                catch (InvalidOperationException)
                {
                }

                throw new InvalidOperationException($"Failed to load plugin descriptor '{uPluginPath}'.", ex);
            }
        }
    }
}
