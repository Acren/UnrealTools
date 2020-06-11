using Microsoft.Win32;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UnrealAutomationCommon;

namespace UnrealLauncher
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public MainWindow()
        {
            InitializeComponent();
            ProjectSource.LoadProjects();
            ProjectGrid.ItemsSource = ProjectSource.Projects;
            DataContext = this;
            Arguments = new UnrealArguments();
        }

        private UnrealArguments arguments;
        public UnrealArguments Arguments
        {
            get { return arguments; }
            set
            {
                if(arguments != value)
                {
                    if (arguments != null)
                        arguments.PropertyChanged -= ArgumentsChanged;
                    arguments = value;
                    if (arguments != null)
                        arguments.PropertyChanged += ArgumentsChanged;
                }
                void ArgumentsChanged(object sender, PropertyChangedEventArgs args)
                {
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(VisibleCommand));
                }
            }
        }

        public string Status
        {
            get
            {
                if (IsProjectSelected)
                {
                    return "Selected " + GetSelectedProject().Name;
                }
                return "Select a project";
            }
        }

        public bool IsProjectSelected
        {
            get
            {
                return ProjectGrid.SelectedItem != null && ProjectGrid.SelectedItem.GetType() == typeof(Project);
            }
        }

        public string VisibleCommand
        {
            get
            {
                if(!IsProjectSelected)
                {
                    return null;
                }
                return CommandUtils.FormatCommand(OpenEditor.GetFileString(GetSelectedProject().ProjectDefinition.GetEngineInstallDirectory()), OpenEditor.GetArgsString(GetSelectedProject().UProjectPath, Arguments));
            }
        }

        public Project GetSelectedProject()
        {
            if(IsProjectSelected)
            {
                return (Project)ProjectGrid.SelectedItem;
            }
            return null;
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

        private void ProjectGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(IsProjectSelected));
            OnPropertyChanged(nameof(VisibleCommand));
        }

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private void LaunchEditor_Executed(object sender, RoutedEventArgs e)
        {
            if (!IsProjectSelected)
            {
                return;
            }
            Project SelectedProject = GetSelectedProject();
            OpenEditor.Open(SelectedProject.ProjectDefinition.GetEngineInstallDirectory(), SelectedProject.UProjectPath, Arguments);
        }
    }
}
