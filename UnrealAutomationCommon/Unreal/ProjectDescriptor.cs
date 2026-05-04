using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalAutomation.Core;
using Newtonsoft.Json;

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
        /// <summary>
        /// Gets or sets the Unreal project descriptor file-format version.
        /// </summary>
        public int FileVersion { get; set; } = 3;

        private string _engineAssociation = string.Empty;

        /// <summary>
        /// Gets or sets the engine association stored in the .uproject descriptor.
        /// </summary>
        public string EngineAssociation
        {
            get => _engineAssociation;
            set
            {
                _engineAssociation = value;
                Engine = EngineFinder.GetEngineInstall(EngineAssociation, true);
            }
        }

        /// <summary>
        /// Gets or sets the project module declarations stored in the descriptor.
        /// </summary>
        public List<ModuleDeclaration> Modules { get; set; } = new();

        /// <summary>
        /// Gets or sets the project plugin dependency declarations stored in the descriptor.
        /// </summary>
        public List<ProjectPluginDependency> Plugins { get; set; } = new();

        /// <summary>
        /// Gets a display-friendly engine name for UI surfaces without serializing it into descriptor files.
        /// </summary>
        [JsonIgnore]
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

        /// <summary>
        /// Gets the resolved engine instance for the current engine association.
        /// </summary>
        [JsonIgnore]
        public Engine Engine { get; private set; } = null!;

        /// <summary>
        /// Gets the editor target name implied by the descriptor's module declarations.
        /// </summary>
        [JsonIgnore]
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

        /// <summary>
        /// Creates a minimal descriptor for a generated empty project.
        /// </summary>
        public static ProjectDescriptor CreateEmpty(EngineVersion? engineVersion = null)
        {
            // Generated projects start as descriptor-only shells; callers add modules or plugin dependencies explicitly.
            ProjectDescriptor descriptor = new()
            {
                FileVersion = 3
            };
            if (engineVersion != null)
            {
                descriptor.EngineAssociation = engineVersion.MajorMinorString;
            }

            return descriptor;
        }

        public static ProjectDescriptor Load(string uProjectPath)
        {
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ProjectDescriptor.Load")
                .SetTag("descriptor.path", uProjectPath);
            return JsonConvert.DeserializeObject<ProjectDescriptor>(File.ReadAllText(uProjectPath))
                ?? throw new InvalidOperationException($"Could not deserialize project descriptor '{uProjectPath}'.");
        }

        /// <summary>
        /// Saves this descriptor to disk using the .uproject JSON shape Unreal expects.
        /// </summary>
        public void Save(string uProjectPath)
        {
            // Json.NET owns the object-to-descriptor boundary; ShouldSerialize methods below keep empty optional sections out.
            File.WriteAllText(uProjectPath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        /// <summary>
        /// Adds or updates one plugin dependency declaration in this descriptor.
        /// </summary>
        public void SetPluginEnabled(string pluginName, bool enabled)
        {
            // Disabled entries are still meaningful because they can override plugins that are enabled by default.
            string resolvedPluginName = RequireText(pluginName, nameof(pluginName), "Plugin name is required.");
            ProjectPluginDependency? existingPlugin = Plugins.FirstOrDefault(plugin => string.Equals(plugin.Name, resolvedPluginName, StringComparison.OrdinalIgnoreCase));
            if (existingPlugin != null)
            {
                existingPlugin.Enabled = enabled;
                return;
            }

            Plugins.Add(new ProjectPluginDependency
            {
                Name = resolvedPluginName,
                Enabled = enabled
            });
        }

        public bool IsEngineInstalled()
        {
            return !EngineAssociation.StartsWith("{");
        }

        public bool HasPluginEnabled(string PluginName)
        {
            return Plugins.Any(Plugin => Plugin.Name == PluginName && Plugin.Enabled);
        }

        /// <summary>
        /// Returns whether the descriptor should write an EngineAssociation property.
        /// </summary>
        public bool ShouldSerializeEngineAssociation()
        {
            return !string.IsNullOrWhiteSpace(EngineAssociation);
        }

        /// <summary>
        /// Returns whether the descriptor should write a Modules array.
        /// </summary>
        public bool ShouldSerializeModules()
        {
            return Modules.Count > 0;
        }

        /// <summary>
        /// Returns whether the descriptor should write a Plugins array.
        /// </summary>
        public bool ShouldSerializePlugins()
        {
            return Plugins.Count > 0;
        }

        /// <summary>
        /// Returns non-empty text or throws with a caller-specific validation message.
        /// </summary>
        private static string RequireText(string value, string parameterName, string message)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException(message, parameterName)
                : value;
        }
    }
}
