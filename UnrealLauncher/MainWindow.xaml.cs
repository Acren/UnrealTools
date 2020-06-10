using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace UnrealLauncher
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            ProjectSource.LoadProjects();
            ProjectGrid.ItemsSource = ProjectSource.Projects;
        }

        private void DataGridCell_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            DataGridCell Cell = (DataGridCell)sender;

            DataGridColumn NameColumn = ProjectGrid.Columns.First(Column => (string)Column.Header == "Path");
            if(Cell.Column == NameColumn)
            {
                OpenFileDialog OpenFileDialog = new OpenFileDialog();
                if (OpenFileDialog.ShowDialog() == true)
                {
                    string SelectedPath = OpenFileDialog.FileName;
                    if (System.IO.Path.GetExtension(SelectedPath).Equals(".uproject", StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (ProjectGrid.SelectedItem.GetType() != typeof(Project))
                        {
                            ProjectGrid.SelectedItem = ProjectSource.AddProject();
                        }
                        Project SelectedProject = (Project)ProjectGrid.SelectedItem;
                        SelectedProject.UProjectPath = SelectedPath;
                    }

                }
            }
        }
    }
}
