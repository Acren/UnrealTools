﻿using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.BaseOperations;
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

            // Default sort
            TargetGrid.Items.SortDescriptions.Clear();
            TargetGrid.Items.SortDescriptions.Add(new SortDescription("TypeName", ListSortDirection.Descending));
            TargetGrid.Items.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
            TargetGrid.Items.Refresh();
        }

        public IOperationTarget SelectedTarget
        {
            get => PersistentState.OperationParameters.Target;
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
                    OnPropertyChanged(nameof(SelectedTarget));
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

        public EngineInstall SelectedEngineInstall => (SelectedTarget as IEngineInstallProvider)?.EngineInstall;

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
                if (SelectedTarget != null)
                {
                    return $"Selected {SelectedTarget.TypeName} {SelectedTarget.Name}";
                }

                return "Select a project or plugin";
            }
        }

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

        private void AddTarget(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            if (openFileDialog.ShowDialog() == true)
            {
                string selectedPath = openFileDialog.FileName;
                PersistentData.Get().AddTarget(selectedPath);
            }
        }

        private void TargetGrid_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            FrameworkElement fe = e.Source as FrameworkElement;
            ContextMenu menu = new ContextMenu();
            fe.ContextMenu = menu;

            menu.Items.Add(new MenuItem() { Header = "Open Directory", Command = new DelegateCommand(o => { RunProcess.OpenDirectory(SelectedTarget.TargetDirectory); }) });
            menu.Items.Add(new MenuItem() { Header = "Open Output", Command = new DelegateCommand(o => { RunProcess.OpenDirectory(SelectedTarget.OutputDirectory); }) });

            menu.Items.Add(new Separator());

            if (SelectedTarget is Project)
            {
                Project project = SelectedTarget as Project;
                menu.Items.Add(new MenuItem() { Header = "Open Staged Build", Command = new DelegateCommand(o => { RunProcess.OpenDirectory(project.GetStagedBuildWindowsPath()); }) });
                menu.Items.Add(new MenuItem() { Header = "Open with Rider", Command = new DelegateCommand(o => { RunProcess.Run(@"C:\Program Files\JetBrains\Rider for Unreal Engine 2020.3.1\bin\rider64.exe",  project.UProjectPath.AddQuotesIfContainsSpace()); }) });
            }

            menu.Items.Add(new Separator());

            menu.Items.Add(new MenuItem() { Header = "Remove", Command = new DelegateCommand(o => { PersistentData.Get().RemoveTarget(SelectedTarget); }) });
        }

        private void TargetGrid_OnSorting(object sender, DataGridSortingEventArgs e)
        {
            
        }
    }
}
