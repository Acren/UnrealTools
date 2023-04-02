using System;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnrealAutomationCommon.Unreal
{
    public class ProjectPluginDependency
    {
        public string Name { get; set; }
        public bool Enabled { get; set; }
    }

    public class ProjectDescriptor
    {
        private string _engineAssociation;

        public string EngineAssociation
        {
            get => _engineAssociation;
            set
            {
                _engineAssociation = value;
                EngineInstall = EngineInstallFinder.GetEngineInstall(EngineAssociation, true);
            }
        }

        public List<ProjectPluginDependency> Plugins { get; set; }

        public string EngineFriendlyName
        {
            get
            {
                if (IsEngineInstalled())
                {
                    return EngineAssociation;
                }

                if (EngineInstall == null)
                {
                    return EngineAssociation;
                }

                return EngineInstall.InstallDirectory;
            }
        }

        public EngineInstall EngineInstall { get; private set; }

        public static ProjectDescriptor Load(string uProjectPath)
        {
            return JsonConvert.DeserializeObject<ProjectDescriptor>(File.ReadAllText(uProjectPath));
        }

        public bool IsEngineInstalled()
        {
            return !EngineAssociation.StartsWith("{");
        }

        public bool HasPluginEnabled(string PluginName)
        {
            return Plugins.Any(Plugin => Plugin.Name == PluginName && Plugin.Enabled);
        }
    }
}