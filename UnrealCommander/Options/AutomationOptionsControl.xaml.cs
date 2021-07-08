using System.Windows;
using System.Windows.Controls;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealCommander.Options
{
    /// <summary>
    /// Interaction logic for AutomationOptionsControl.xaml
    /// </summary>
    public partial class AutomationOptionsControl : OptionsUserControl
    {
        public static readonly DependencyProperty TestNameProperty = DependencyProperty.Register(nameof(TestName),typeof(string),typeof(AutomationOptionsControl), new FrameworkPropertyMetadata(
            string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        private AutomationOptions _options = null;

        public AutomationOptions Options
        {
            get => _options;
            set
            {
                _options = value;
                OnPropertyChanged();
            }
        }

        public string TestName
        {
            get => (string)GetValue(TestNameProperty);
            set
            {
                SetValue(TestNameProperty, value);
                OnPropertyChanged();
            }
        }

        public AutomationOptionsControl()
        {
            InitializeComponent();
        }

    }
}
