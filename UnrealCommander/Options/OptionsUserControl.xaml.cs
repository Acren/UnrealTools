using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using UnrealCommander.Annotations;

namespace UnrealCommander.Options
{
    /// <summary>
    /// Interaction logic for OptionsUserControl.xaml
    /// </summary>
    public partial class OptionsUserControl : UserControl, IOptionsControl, INotifyPropertyChanged
    {
        private IOptionsDataProvider _dataProvider = null;

        public IOptionsDataProvider DataProvider
        {
            get => _dataProvider;
            set
            {
                _dataProvider = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
