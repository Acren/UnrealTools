using System.Windows;

namespace UnrealCommander.Options
{
    /// <summary>
    /// Interaction logic for AutomationOptionsControl.xaml
    /// </summary>
    public partial class AutomationOptionsControl : OptionsUserControl
    {
        //public static readonly DependencyProperty TestNameProperty = DependencyProperty.Register(nameof(TestName),typeof(string),typeof(AutomationOptionsControl), new FrameworkPropertyMetadata(
        //    string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        //public string TestName
        //{
        //    get => (string)GetValue(TestNameProperty);
        //    set
        //    {
        //        SetValue(TestNameProperty, value);
        //        OnPropertyChanged();
        //    }
        //}

        public AutomationOptionsControl()
        {
            InitializeComponent();
        }

    }
}
