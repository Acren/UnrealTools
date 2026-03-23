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
        public string VersionName { get; set; }
        public string FriendlyName { get; set; }
        public bool IsBetaVersion { get; set; }
        public string EngineVersionString { get; set; }

        public List<ModuleDeclaration> Modules { get; set; } = new();

        public SemVersion SemVersion => SemVersion.Parse(VersionName, SemVersionStyles.Strict);
        public EngineVersion EngineVersion => string.IsNullOrEmpty(EngineVersionString) ? null : new(EngineVersionString);

        public static PluginDescriptor Load(string uPluginPath)
        {
            using OperationSwitchActivityScope activity = OperationSwitchTelemetry.StartActivity("PluginDescriptor.Load");
            OperationSwitchTelemetry.SetTag(activity, "descriptor.path", uPluginPath);
            FileUtils.WaitForFileReadable(uPluginPath);
            try
            {
                return JsonConvert.DeserializeObject<PluginDescriptor>(File.ReadAllText(uPluginPath));
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

                return null;
            }
        }
    }
}
