using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations;

namespace UnrealCommander
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private PersistentData _persistentState;

        private Operation _operation;
        private string _output;

        public event PropertyChangedEventHandler PropertyChanged;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            PersistentState = PersistentData.Load();
        }

        public Project SelectedProject
        {
            get => PersistentState.OperationParameters.Target as Project;
            set
            {
                if (PersistentState.OperationParameters.Target != value)
                {
                    PersistentState.OperationParameters.Target = value;
                    OnPropertyChanged();
                }
            }
        }

        public Plugin SelectedPlugin
        {
            get => PersistentState.OperationParameters.Target as Plugin;
            set
            {
                if (PersistentState.OperationParameters.Target != value)
                {
                    PersistentState.OperationParameters.Target = value;
                    OnPropertyChanged();
                }
            }
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
                    PersistentStateChanged(this, null);
                }
                void PersistentStateChanged(object sender, PropertyChangedEventArgs args)
                {
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(VisibleCommand));
                    OnPropertyChanged(nameof(CanExecute));
                    OnPropertyChanged(nameof(Status));
                    OnPropertyChanged(nameof(SelectedPlugin));
                    OnPropertyChanged(nameof(SelectedProject));

                    if (!Operation.OperationTypeSupportsTarget(PersistentState.OperationType,
                        PersistentState.OperationParameters.Target))
                    {
                        foreach (Type operationType in OperationTypes)
                        {
                            if (Operation.OperationTypeSupportsTarget(operationType,
                                PersistentState.OperationParameters.Target))
                            {
                                PersistentState.OperationType = operationType;
                                break;
                            }
                        }
                    }

                    if (typeof(Operation) != PersistentState.OperationType)
                    {
                        Operation = Operation.CreateOperation(PersistentState.OperationType);
                    }
                }
            }
        }

        public Operation Operation
        {
            get => _operation;
            set
            {
                _operation = value;
                if (!_operation.SupportsConfiguration(PersistentState.OperationParameters.Configuration))
                {
                    // Set different configuration
                    foreach(BuildConfiguration config in BuildConfigurations)
                    {
                        if (!_operation.SupportsConfiguration(config)) continue;

                        EngineInstall install = _operation.GetRelevantEngineInstall(PersistentState.OperationParameters);

                        if (install != null && !install.SupportsConfiguration(config)) continue;

                        PersistentState.OperationParameters.Configuration = config;
                        break;
                    }
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(VisibleCommand));
                OnPropertyChanged(nameof(CanExecute));
            }
        }

        public List<Type> OperationTypes => OperationList.GetOrderedOperationTypes();

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

        public bool IsProjectSelected => PersistentState.OperationParameters.Target is Project;
        //ProjectGrid.SelectedItem != null && ProjectGrid.SelectedItem.GetType() == typeof(Project);

        public bool IsPluginSelected => PersistentState.OperationParameters.Target is Plugin;
        //PluginGrid.SelectedItem != null && PluginGrid.SelectedItem.GetType() == typeof(Plugin);

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
            return PersistentState.OperationParameters.Target as Project;
        }

        private Plugin GetSelectedPlugin()
        {
            return PersistentState.OperationParameters.Target as Plugin;
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
