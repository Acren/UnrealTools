using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealCommander.Options
{
    /// <summary>
    /// Interaction logic for PackageOptionsControl.xaml
    /// </summary>
    public partial class PackageOptionsControl : OptionsUserControl
    {
        private PackageOptions _options = null;

        public PackageOptions Options
        {
            get => _options;
            set
            {
                _options = value;
                OnPropertyChanged();
            }
        }

        public PackageOptionsControl()
        {
            InitializeComponent();
        }
    }
}
