﻿using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Operations.OperationTypes;
using UnrealAutomationCommon.Unreal;
using UnrealCommander.Options;

namespace UnrealCommander
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private PersistentData _persistentState = new PersistentData();

        private Operation _operation = new LaunchEditor();

        private OperationRunner _runningOperation = null;

        public event PropertyChangedEventHandler PropertyChanged;

        public MainWindow()
        {
            InitializeComponent();
            PersistentState = PersistentData.Load();

            BuildConfigurationOptionsControlElement.Options = PersistentState.OperationParameters.RequestOptions<BuildConfigurationOptions>();
            InsightsOptionsControlElement.Options = PersistentState.OperationParameters.RequestOptions<InsightsOptions>();
            FlagOptionsControlElement.Options = PersistentState.OperationParameters.RequestOptions<FlagOptions>();
            AutomationOptionsControlElement.Options = PersistentState.OperationParameters.RequestOptions<AutomationOptions>();
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
                    OnPropertyChanged(nameof(AllowedBuildConfigurations));
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
                    OnPropertyChanged(nameof(AllowedBuildConfigurations));
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
                    OnPropertyChanged(nameof(OperationTarget));

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
                SelectSupportedBuildConfiguration();
                OnPropertyChanged();
                OnPropertyChanged(nameof(VisibleCommand));
                OnPropertyChanged(nameof(CanExecute));
                OnPropertyChanged(nameof(AllowedBuildConfigurations));
                OnPropertyChanged(nameof(EnabledOptionSets));
            }
        }

        private void SelectSupportedBuildConfiguration()
        {
            if (AllowedBuildConfigurations.Configurations.Count > 0 && !AllowedBuildConfigurations.Configurations.Contains(PersistentState.OperationParameters
                .RequestOptions<BuildConfigurationOptions>().Configuration))
            {
                PersistentState.OperationParameters.RequestOptions<BuildConfigurationOptions>().Configuration =
                    AllowedBuildConfigurations.Configurations[0];
            }
        }

        public IOperationTarget OperationTarget => PersistentState.OperationParameters.Target;

        public List<Type> OperationTypes => OperationList.GetOrderedOperationTypes();

        public EngineInstall SelectedEngineInstall =>
            IsProjectSelected ? GetSelectedProject().ProjectDescriptor.GetEngineInstall() :
                IsPluginSelected ? GetSelectedPlugin().PluginDescriptor.GetEngineInstall() :
                null;

        public AllowedBuildConfigurations AllowedBuildConfigurations
        {
            get
            {
                if (OperationTarget == null)
                {
                    return new AllowedBuildConfigurations();
                }

                return new AllowedBuildConfigurations()
                {
                    Configurations = EnumUtils.GetAll<BuildConfiguration>().Where(c =>
                        _operation.SupportsConfiguration(c) && OperationTarget
                            .SupportsConfiguration(c)).ToList()
                };
            }
        }

        public List<Type> EnabledOptionSets => Operation.GetRequiredOptionSetTypes(PersistentState.OperationParameters.Target)?.ToList();

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

        public bool IsPluginSelected => PersistentState.OperationParameters.Target is Plugin;

        public bool CanExecute => Operation.RequirementsSatisfied(PersistentState.OperationParameters);

        public string VisibleCommand
        {
            get
            {
                List<string> commandStrings = new List<string>();
                foreach (Command command in Operation.GetCommands(PersistentState.OperationParameters))
                {
                    commandStrings.Add(command.ToString());
                }

                if (commandStrings.Count > 0)
                {
                    return string.Join("\n", commandStrings);
                }

                return "No command";
            }
        }

        private int LineCount { get; set; }

        public OperationRunner RunningOperation
        {
            get => _runningOperation;
            set
            {
                if(_runningOperation != value)
                {
                    _runningOperation = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsRunningOperation));
                }
            }
        }

        public bool IsRunningOperation
        {
            get => RunningOperation != null;
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
                if (IsRunningOperation)
                {
                    MessageBoxResult result = MessageBox.Show("Process is running. Terminate it?", "Terminate process", MessageBoxButton.YesNoCancel);
                    switch (result)
                    {
                        case MessageBoxResult.Yes:
                            RunningOperation.Terminate();
                            break;
                        case MessageBoxResult.No:
                            break;
                        case MessageBoxResult.Cancel:
                            return;
                    }
                }

                AddOutputLine($"User started operation '{Operation.OperationName}'");

                OperationRunner newRunner = new OperationRunner(Operation, PersistentState.OperationParameters);
                newRunner.Output += (S, verbosity) =>
                {
                    // Output handler
                    Dispatcher.Invoke(() =>
                    {
                        // Prepend line numbers to each line of the output.
                        if (!String.IsNullOrEmpty(S))
                        {
                            AddOutputLine(S, verbosity);
                        }
                    });
                };

                _ = RunOperation(newRunner);
            }
        }

        private async Task RunOperation(OperationRunner runner)
        {
            if (RunningOperation != null)
            {
                AddOutputLine("Already running an operation", LogVerbosity.Error);
                return;
            }

            RunningOperation = runner;
            try
            {
                Task task = runner.Run();
                await task;
            }
            catch (Exception e)
            {
                AddOutputLine(e.ToString(), LogVerbosity.Error);
            }

            FlashWindow.Flash(Process.GetCurrentProcess().MainWindowHandle);
            RunningOperation = null;
        }

        private void Terminate(object sender, RoutedEventArgs e)
        {
            if(RunningOperation != null)
            {
                RunningOperation.Terminate();
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

        private void AddOutputLine(string line, LogVerbosity verbosity = LogVerbosity.Log)
        {
            LineCount++;
            string finalLine = "[" + $"{DateTime.Now:u}" + "][" + LineCount + @"]: " + line + "\r";

            TextRange tr = new TextRange(OutputTextBox.Document.ContentEnd, OutputTextBox.Document.ContentEnd);
            tr.Text = finalLine;

            Color color;

            switch(verbosity)
            {
                case LogVerbosity.Log:
                    color = Colors.White;
                    break;
                case LogVerbosity.Warning:
                    color = Color.FromRgb(230, 230, 10);
                    break;
                case LogVerbosity.Error:
                    color = Color.FromRgb(255, 80, 80);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(verbosity), verbosity, null);
            }

            tr.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(color));

            if (OutputScrollViewer.VerticalOffset == OutputScrollViewer.ScrollableHeight)
            {
                OutputScrollViewer.ScrollToEnd();
            }

        }

        private void CopyCommand(object sender, RoutedEventArgs e)
        {
            Clipboard.SetDataObject(Operation.GetCommands(PersistentState.OperationParameters).First().ToString());
            CommandTextBox.Focus();
            CommandTextBox.SelectAll();
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
            OutputTextBox.Document.Blocks.Clear();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if(IsRunningOperation)
            {
                MessageBoxResult result = MessageBox.Show("Process is running. Terminate it?", "Terminate process", MessageBoxButton.YesNoCancel);
                switch(result)
                {
                    case MessageBoxResult.Yes:
                        // Check is running again, because it may have finished while the message box was open
                        if (IsRunningOperation)
                        {
                            RunningOperation.Terminate();

                            // Small sleep so that dispatch invokes triggered by termination don't crash
                            Thread.Sleep(1);
                        }
                        break;
                    case MessageBoxResult.No:
                        break;
                    case MessageBoxResult.Cancel:
                        e.Cancel = true;
                        break;
                }
            }
        }
    }
}
