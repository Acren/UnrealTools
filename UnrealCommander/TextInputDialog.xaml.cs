using System.Windows;

namespace UnrealCommander
{
    /// <summary>
    /// Interaction logic for TextInputDialog.xaml
    /// </summary>
    public partial class TextInputDialog : Window
    {
        public TextInputDialog(string title)
        {
            Title = title;
            DataContext = this;
            InitializeComponent();
        }

        public string InputValue { get; set; }

        private void OkButton_OnClick(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
