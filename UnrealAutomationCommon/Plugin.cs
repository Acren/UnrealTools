using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon
{
    public class Plugin : OperationTarget
    {
        private string _uPluginPath;

        public Plugin()
        {
        }

        public Plugin(string Path)
        {
            UPluginPath = Path;
            if (PluginUtils.IsPluginFile(UPluginPath))
            {
                LoadDescriptor();
            }
        }

        public string UPluginPath
        {
            get => _uPluginPath;
            set
            {
                _uPluginPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Name));
            }
        }

        [JsonIgnore]
        public PluginDescriptor PluginDescriptor { get; private set; }

        public string Name => Path.GetFileNameWithoutExtension(UPluginPath) ?? "Invalid";

        public override void LoadDescriptor()
        {
            PluginDescriptor = PluginDescriptor.Load(UPluginPath);
        }

        public string GetPluginPath()
        {
            return Path.GetDirectoryName(_uPluginPath);
        }

        public override string GetName()
        {
            return Name;
        }

        public override EngineInstall GetEngineInstall()
        {
            return PluginDescriptor.GetEngineInstall();
        }
    }
}
