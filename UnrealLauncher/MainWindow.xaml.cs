using System;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
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
        private OperationType _operationType;
        private Operation _operation;
        private OperationParameters _operationParameters;
        private string _output;

        public event PropertyChangedEventHandler PropertyChanged;

        public MainWindow()
        {
            InitializeComponent();
            PersistentData.Load();
            ProjectGrid.ItemsSource = PersistentData.Get().Projects;
            PluginGrid.ItemsSource = PersistentData.Get().Plugins;
            DataContext = this;
            OperationParameters = new OperationParameters
            {
                OutputPathRoot = "C:\\UnrealLauncher",
                UseOutputPathProjectSubfolder = true,
                UseOutputPathOperationSubfolder = true
            };
            OperationType = OperationType.OpenEditor;
        }

        public OperationType OperationType
        {
            get => _operationType;
            set
            {
                _operationType = value;
                Operation = Operation.CreateOperation(OperationType);
                OnPropertyChanged();
            }
        }

        public Operation Operation
        {
            get => _operation;
            set
            {
                _operation = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VisibleCommand));
                OnPropertyChanged(nameof(CanExecute));
            }
        }

        public OperationParameters OperationParameters
        {
            get => _operationParameters;
            set
            {
                if(_operationParameters != value)
                {
                    if (_operationParameters != null)
                        _operationParameters.PropertyChanged -= OperationParametersChanged;
                    _operationParameters = value;
                    if (_operationParameters != null)
                        _operationParameters.PropertyChanged += OperationParametersChanged;
                }
                void OperationParametersChanged(object sender, PropertyChangedEventArgs args)
                {
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(VisibleCommand));
                    OnPropertyChanged(nameof(CanExecute));
                }
            }
        }

        public string Status
        {
            get
            {
                if (IsProjectSelected)
                {
                    return "Selected project " + GetSelectedProject().Name;
                }

                if (IsPluginSelected)
                {
                    return "Selected plugin " + GetSelectedPlugin().Name;
                }
                return "Select a project or plugin";
            }
        }

        public bool IsProjectSelected => ProjectGrid.SelectedItem != null && ProjectGrid.SelectedItem.GetType() == typeof(Project);

        public bool IsPluginSelected => PluginGrid.SelectedItem != null && PluginGrid.SelectedItem.GetType() == typeof(Plugin);

        public bool CanExecute => Operation.RequirementsSatisfied(OperationParameters);

        public string VisibleCommand
        {
            get
            {
                Command command = Operation.GetCommand(OperationParameters);
                return command != null ? command.ToString() : "No command";
            }
        }

        private int LineCount { get; set; }
        private int ProcessLineCount { get; set; }

        public string Output
        {
            get => _output;
            set
            {
                _output = value;
                OnPropertyChanged();
            }
        }

        private Project GetSelectedProject()
        {
            if(IsProjectSelected)
            {
                return (Project)ProjectGrid.SelectedItem;
            }
            return null;
        }

        private Plugin GetSelectedPlugin()
        {
            if (IsPluginSelected)
            {
                return (Plugin) PluginGrid.SelectedItem;
            }

            return null;
        }

        private void ProjectDoubleClick(object sender, MouseButtonEventArgs e)
        {
            DataGridCell cell = (DataGridCell)sender;

            DataGridColumn nameColumn = ProjectGrid.Columns.First(Column => (string)Column.Header == "Path");
            if(cell.Column == nameColumn)
            {
                OpenFileDialog openFileDialog = new OpenFileDialog();
                if (openFileDialog.ShowDialog() == true)
                {
                    string selectedPath = openFileDialog.FileName;
                    if (ProjectUtils.IsProjectFile(selectedPath))
                    {
                        if (ProjectGrid.SelectedItem.GetType() != typeof(Project))
                        {
                            // Create new project
                            ProjectGrid.SelectedItem = PersistentData.Get().AddProject(selectedPath);
                        }
                        else
                        {
                            // Update existing
                            Project selectedProject = (Project)ProjectGrid.SelectedItem;
                            selectedProject.UProjectPath = selectedPath;
                        }
                    }

                }
            }
        }

        private void PluginDoubleClick(object sender, MouseButtonEventArgs e)
        {
            DataGridCell cell = (DataGridCell)sender;

            DataGridColumn nameColumn = PluginGrid.Columns.First(Column => (string)Column.Header == "Path");
            if (cell.Column == nameColumn)
            {
                OpenFileDialog openFileDialog = new OpenFileDialog();
                if (openFileDialog.ShowDialog() == true)
                {
                    string selectedPath = openFileDialog.FileName;
                    if (PluginUtils.IsPluginFile(selectedPath))
                    {
                        if (PluginGrid.SelectedItem.GetType() != typeof(Plugin))
                        {
                            // Create new project
                            PluginGrid.SelectedItem = PersistentData.Get().AddPlugin(selectedPath);
                        }
                        else
                        {
                            // Update existing
                            Plugin selectedPlugin = (Plugin)PluginGrid.SelectedItem;
                            selectedPlugin.UPluginPath = selectedPath;
                        }
                    }

                }
            }
        }

        private void ProjectGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsProjectSelected)
            {
                PluginGrid.SelectedItem = null;
            }
            OperationParameters.Project = IsProjectSelected ? GetSelectedProject() : null;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(IsProjectSelected));
            OnPropertyChanged(nameof(IsPluginSelected));
            OnPropertyChanged(nameof(VisibleCommand));
            OnPropertyChanged(nameof(CanExecute));
        }

        private void PluginGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if(IsPluginSelected)
            {
                ProjectGrid.SelectedItem = null;
            }
            OperationParameters.Plugin = IsPluginSelected ? GetSelectedPlugin() : null;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(IsProjectSelected));
            OnPropertyChanged(nameof(IsPluginSelected));
            OnPropertyChanged(nameof(VisibleCommand));
            OnPropertyChanged(nameof(CanExecute));
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private void Execute(object sender, RoutedEventArgs e)
        {
            if (Operation.RequirementsSatisfied(OperationParameters))
            {
                ProcessLineCount = 0;
                AddOutputLine("Running command: " + Operation.GetCommand(OperationParameters));
                Operation.Execute(OperationParameters, new DataReceivedEventHandler((handlerSender, e) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        // Prepend line numbers to each line of the output.
                        if (!String.IsNullOrEmpty(e.Data))
                        {
                            ProcessLineCount++;
                            AddOutputLine("[" + ProcessLineCount + "]: " + e.Data);
                        }
                    });

                }));
            }
        }

        private void ProjectRemoveClick(object sender, RoutedEventArgs e)
        {
            PersistentData.Get().RemoveProject(GetSelectedProject());
        }

        private void PluginRemoveClick(object sender, RoutedEventArgs e)
        {
            PersistentData.Get().RemovePlugin(GetSelectedPlugin());
        }

        private void AddOutputLine(string line)
        {
            LineCount++;
            Output += "[" + $"{DateTime.Now:u}" + "][" + LineCount + @"]: " + line + "\n";
            OutputTextBox.ScrollToEnd();
        }
    }
}
