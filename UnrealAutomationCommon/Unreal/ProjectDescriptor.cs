using System;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalAutomation.Core;

namespace UnrealAutomationCommon.Unreal
{
    public class ModuleDeclaration
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
    }

    public class ProjectPluginDependency
    {
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; }
    }

    public class ProjectDescriptor
    {
        private string _engineAssociation = string.Empty;

        public string EngineAssociation
        {
            get => _engineAssociation;
            set
            {
                _engineAssociation = value;
                Engine = EngineFinder.GetEngineInstall(EngineAssociation, true);
            }
        }

        public List<ModuleDeclaration> Modules { get; set; } = new();
        public List<ProjectPluginDependency> Plugins { get; set; } = new();

        public string EngineFriendlyName
        {
            get
            {
                if (IsEngineInstalled())
                {
                    return EngineAssociation;
                }

                if (Engine == null)
                {
                    return EngineAssociation;
                }

                return Engine.TargetPath;
            }
        }

        public Engine Engine { get; private set; } = null!;
            
        public string EditorTargetName
        {
            get
            {
                // Try find an editor module
                var editorModule = Modules.Find(m => m.Type == "Editor");
                if (editorModule != null)
                {
                    return editorModule.Name;
                }

                // If there is no explicitly defined editor module, append "Editor" to the first module
                if (Modules.Count == 0)
                {
                    throw new InvalidOperationException("Project descriptor does not define any modules.");
                }

                return Modules[0].Name + "Editor";
            }
        }

        public static ProjectDescriptor Load(string uProjectPath)
        {
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ProjectDescriptor.Load")
                .SetTag("descriptor.path", uProjectPath);
            return JsonConvert.DeserializeObject<ProjectDescriptor>(File.ReadAllText(uProjectPath))
                ?? throw new InvalidOperationException($"Could not deserialize project descriptor '{uProjectPath}'.");
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
