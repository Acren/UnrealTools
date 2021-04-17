using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace UnrealAutomationCommon
{
    public class Plugin : INotifyPropertyChanged
    {
        private string _uPluginPath;
        public event PropertyChangedEventHandler PropertyChanged;

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

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public void LoadDescriptor()
        {
            PluginDescriptor = PluginDescriptor.Load(UPluginPath);
        }

        public string GetPluginPath()
        {
            return Path.GetDirectoryName(_uPluginPath);
        }
    }
}
