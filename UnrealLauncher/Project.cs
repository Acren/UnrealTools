using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace UnrealLauncher
{
    public class Project : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public string Name
        {
            get
            {
                return Path.GetFileNameWithoutExtension(UProjectPath) ?? "Invalid";
            }
        }

        private string uProjectPath;
        public string UProjectPath
        {
            get { return uProjectPath; }
            set
            {
                uProjectPath = value;
                OnPropertyChanged();
                OnPropertyChanged("Name");
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
