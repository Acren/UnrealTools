using System.IO;
using Newtonsoft.Json;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    public class Plugin : OperationTarget
    {
        private string _uPluginPath;

        // Default constructor is needed to support adding rows from DataGrid
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
            return PluginDescriptor?.GetEngineInstall();
        }
    }
}
