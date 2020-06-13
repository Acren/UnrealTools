using Microsoft.Win32;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations;

namespace UnrealLauncher
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private OperationType operationType;
        private Operation operation;
        private OperationParameters operationParameters;

        public event PropertyChangedEventHandler PropertyChanged;

        public MainWindow()
        {
            InitializeComponent();
            PersistentData.Load();
            ProjectGrid.ItemsSource = PersistentData.Get().Projects;
            DataContext = this;
            OperationParameters = new OperationParameters();
            OperationType = OperationType.OpenEditor;
        }

        public OperationType OperationType
        {
            get { return operationType; }
            set
            {
                operationType = value;
                Operation = Operation.CreateOperation(OperationType);
                OnPropertyChanged();
            }
        }

        public Operation Operation
        {
            get { return operation; }
            set
            {
                operation = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VisibleCommand));
            }
        }

        public OperationParameters OperationParameters
        {
            get { return operationParameters; }
            set
            {
                if(operationParameters != value)
                {
                    if (operationParameters != null)
                        operationParameters.PropertyChanged -= OperationParametersChanged;
                    operationParameters = value;
                    if (operationParameters != null)
                        operationParameters.PropertyChanged += OperationParametersChanged;
                }
                void OperationParametersChanged(object sender, PropertyChangedEventArgs args)
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
                return Operation.GetCommand(OperationParameters).ToString();
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
                            ProjectGrid.SelectedItem = PersistentData.Get().AddProject(SelectedPath);
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
            OperationParameters.Project = IsProjectSelected ? GetSelectedProject() : null;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(IsProjectSelected));
            OnPropertyChanged(nameof(VisibleCommand));
        }

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private void Execute(object sender, RoutedEventArgs e)
        {
            if (!IsProjectSelected)
            {
                return;
            }
            operation.Execute(OperationParameters);
        }
    }
}
