using Microsoft.Win32;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UnrealAutomationCommon;

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
                    if (ProjectDefinition.IsProjectFile(SelectedPath))
                    {
                        if (ProjectGrid.SelectedItem.GetType() != typeof(Project))
                        {
                            // Create new project
                            ProjectGrid.SelectedItem = ProjectSource.AddProject(SelectedPath);
                        }
                        else
                        {
                            // Update existing
                            Project SelectedProject = (Project)ProjectGrid.SelectedItem;
                            SelectedProject.UProjectPath = SelectedPath;
                        }
                    }

                }
            }
        }
    }
}
