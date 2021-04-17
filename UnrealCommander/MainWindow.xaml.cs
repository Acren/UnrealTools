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
using System.Collections.Generic;
using UnrealAutomationCommon.Operations.OperationTypes;

namespace UnrealCommander
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private PersistentData _persistentState;

        private Type _operationType;
        private Operation _operation;
        private string _output;

        public event PropertyChangedEventHandler PropertyChanged;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            PersistentState = PersistentData.Load();

            OperationType = typeof(LaunchEditor);
        }

        public PersistentData PersistentState
        {
            get => _persistentState;
            set
            {
                if (_persistentState != value)
                {
                    if (_persistentState != null)
                        _persistentState.PropertyChanged -= PersistentStateChanged;
                    _persistentState = value;
                    if (_persistentState != null)
                        _persistentState.PropertyChanged += PersistentStateChanged;
                    OnPropertyChanged();
                }
                void PersistentStateChanged(object sender, PropertyChangedEventArgs args)
                {
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(VisibleCommand));
                    OnPropertyChanged(nameof(CanExecute));
                }
            }
        }

        public Type OperationType
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

        public List<Type> Operations => OperationList.GetOrderedOperationTypes();

        public List<BuildConfiguration> BuildConfigurations => Enum.GetValues(typeof(BuildConfiguration)).Cast<BuildConfiguration>().ToList();

        public EngineInstall SelectedEngineInstall =>
            IsProjectSelected ? GetSelectedProject().ProjectDescriptor.GetEngineInstall() : 
                IsPluginSelected ? GetSelectedPlugin().PluginDescriptor.GetEngineInstall() :
                null;

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

        public bool CanExecute => Operation.RequirementsSatisfied(PersistentState.OperationParameters);

        public string VisibleCommand
        {
            get
            {
                Command command = Operation.GetCommand(PersistentState.OperationParameters);
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

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private void Execute(object sender, RoutedEventArgs e)
        {
            if (Operation.RequirementsSatisfied(PersistentState.OperationParameters))
            {
                ProcessLineCount = 0;
                AddOutputLine("Running command: " + Operation.GetCommand(PersistentState.OperationParameters));
                Process process = null;
                process = Operation.Execute(PersistentState.OperationParameters, (handlerSender, e) =>
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

                }, (o, args) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        AddOutputLine("Process exited with code " + process.ExitCode);
                    });
                });
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
            if (OutputScrollViewer.VerticalOffset == OutputScrollViewer.ScrollableHeight)
            {
                OutputScrollViewer.ScrollToEnd();
            }

        }

        private void CopyCommand(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(Operation.GetCommand(PersistentState.OperationParameters).ToString());
        }

        private void ProjectOpenDirectory(object Sender, RoutedEventArgs E)
        {
            RunProcess.OpenDirectory(GetSelectedProject().GetProjectPath());
        }

        private void PluginOpenDirectory(object Sender, RoutedEventArgs E)
        {
            RunProcess.OpenDirectory(GetSelectedPlugin().GetPluginPath());
        }

        private void ProjectOpenStagedBuildWindows(object Sender, RoutedEventArgs E)
        {
            RunProcess.OpenDirectory(GetSelectedProject().GetStagedBuildWindowsPath());
        }

        private void LogClear(object Sender, RoutedEventArgs E)
        {
            Output = "";
        }
    }
}
