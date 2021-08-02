using System.Windows.Controls;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealCommander.Options
{
    /// <summary>
    /// Interaction logic for FlagOptionsControl.xaml
    /// </summary>
    public partial class FlagOptionsControl : OptionsUserControl
    {
        private FlagOptions _options = null;

        public FlagOptions Options
        {
            get => _options;
            set { _options = value; OnPropertyChanged(); }
        }

        public FlagOptionsControl()
        {
            InitializeComponent();
        }

    }
}
