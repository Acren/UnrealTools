using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;
using UnrealCommander.Options;

namespace UnrealCommander
{
    public class TargetSorter : IComparer
    {
        public int Compare(object x, object y)
        {
            IOperationTarget targetX = x as IOperationTarget;
            IOperationTarget targetY = y as IOperationTarget;

            string xName = targetX.RootTarget.DisplayName;
            string yName = targetY.RootTarget.DisplayName;
            int rootNameComp = string.Compare(xName, yName, StringComparison.Ordinal);
            if (rootNameComp != 0)
            {
                return rootNameComp;
            }

            int rootPathComp = string.Compare(targetX.RootTarget.TargetDirectory, targetY.RootTarget.TargetDirectory, StringComparison.Ordinal);
            if (rootPathComp != 0)
            {
                return rootPathComp;
            }

            bool xRoot = targetX.RootTarget.Equals(targetX);
            bool yRoot = targetY.RootTarget.Equals(targetY);
            int rootComp = xRoot.CompareTo(yRoot);
            if (rootComp != 0)
            {
                return rootComp * -1;
            }

            return 0;
        }
    }

    /// <summary>
    ///     Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private Operation _operation;
        private PersistentData _persistentState = new();

        private Runner _running;

        public MainWindow()
        {
            PersistentState = PersistentData.Load();
            InitializeComponent();

            AppLogger.Instance.Output += AddLogToOutputViewer;

            TargetGrid.Sorting += (sender, e) =>
            {
                DataGridColumn column = e.Column;

                // Prevent the built-in sort from sorting
                e.Handled = true;

                ListSortDirection direction = column.SortDirection != ListSortDirection.Ascending ? ListSortDirection.Ascending : ListSortDirection.Descending;

                // Set the sort order on the column
                column.SortDirection = direction;

                // Use a ListCollectionView to do the sort
                ListCollectionView lcv = (ListCollectionView)CollectionViewSource.GetDefaultView(PersistentState.Targets);

                lcv.CustomSort = new TargetSorter();
            };

            // Default sort
            TargetGrid.Items.SortDescriptions.Clear();
            TargetGrid.Items.SortDescriptions.Add(new SortDescription("TypeName", ListSortDirection.Descending));
            TargetGrid.Items.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
            TargetGrid.Items.Refresh();

            // Trigger initial sort
            MethodInfo performSortMethod = typeof(DataGrid)
                .GetMethod("PerformSort",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            performSortMethod?.Invoke(TargetGrid, new[] { TargetGrid.Columns[0] });

            // Refresh selected target, otherwise the grid does not highlight it for reasons currently unknown
            IOperationTarget target = SelectedTarget;
            SelectedTarget = null;
            SelectedTarget = target;

            AppLogger.Instance.Log("App initialized");
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
                    {
                        _persistentState.PropertyChanged -= PersistentStateChanged;
                    }

                    _persistentState = value;
                    if (_persistentState != null)
                    {
                        _persistentState.PropertyChanged += PersistentStateChanged;
                    }

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
                            if (Operation.OperationTypeSupportsTarget(operationType,
                                    PersistentState.OperationParameters.Target))
                            {
                                PersistentState.OperationType = operationType;
                                break;
                            }
                    }

                    if (Operation == null || Operation.GetType() != PersistentState.OperationType)
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
                if (_operation != value)
                {
                    _operation = value;
                    SelectSupportedBuildConfiguration();
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(VisibleCommand));
                    OnPropertyChanged(nameof(CanExecute));
                    OnPropertyChanged(nameof(AllowedBuildConfigurations));
                    OnPropertyChanged(nameof(EnabledOptionSetTypes));
                    OnPropertyChanged(nameof(EnabledOptionSets));
                }
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

                return new AllowedBuildConfigurations
                {
                    Configurations = EnumUtils.GetAll<BuildConfiguration>().Where(c =>
                        _operation.SupportsConfiguration(c) && OperationTarget
                            .SupportsConfiguration(c)).ToList()
                };
            }
        }

        public List<Type> EnabledOptionSetTypes => Operation.GetRequiredOptionSetTypes(PersistentState.OperationParameters.Target).ToList();

        public List<OperationOptions> EnabledOptionSets => PersistentState.OperationParameters.OptionsInstances.Where(x => EnabledOptionSetTypes.Contains(x.GetType())).ToList();

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
                if (Operation == null)
                {
                    return "No operation";
                }

                var commandStrings = new List<string>();
                foreach (Command command in Operation.GetCommands(PersistentState.OperationParameters)) commandStrings.Add(command.ToString());

                if (commandStrings.Count > 0)
                {
                    return string.Join("\n", commandStrings);
                }

                return "No command";
            }
        }

        private int LineCount { get; set; }

        public Runner Running
        {
            get => _running;
            set
            {
                if (_running != value)
                {
                    _running = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsRunningOperation));
                }
            }
        }

        public bool IsRunningOperation => Running is { IsRunning: true };

        public event PropertyChangedEventHandler PropertyChanged;

        private void SelectSupportedBuildConfiguration()
        {
            if (AllowedBuildConfigurations.Configurations.Count > 0 && !AllowedBuildConfigurations.Configurations.Contains(PersistentState.OperationParameters
                .RequestOptions<BuildConfigurationOptions>().Configuration))
            {
                PersistentState.OperationParameters.RequestOptions<BuildConfigurationOptions>().Configuration =
                    AllowedBuildConfigurations.Configurations[0];
            }
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private void Execute(object sender, RoutedEventArgs e)
        {
            _ = ExecuteAsync();
        }

        private async Task ExecuteAsync()
        {
            // Create a new operation instance of the selected one
            Operation newOperation = Operation.CreateOperation(Operation.GetType());

            if (!newOperation.RequirementsSatisfied(PersistentState.OperationParameters))
            {
                return;
            }

            if (IsRunningOperation)
            {
                MessageBoxResult result = MessageBox.Show($"Operation '{Running.Operation.OperationName}' is running. Terminate it?", "Terminate operation", MessageBoxButton.YesNoCancel);
                switch (result)
                {
                    case MessageBoxResult.Yes:
                        await Running.Cancel();
                        break;
                    case MessageBoxResult.No:
                        break;
                    case MessageBoxResult.Cancel:
                        return;
                }
            }

            if (IsRunningOperation)
            {
                AppLogger.Instance.Log("Already running an operation", LogVerbosity.Error);
                return;
            }

            AppLogger.Instance.Log($"User started operation '{newOperation.OperationName}'");

            Runner newRunner = new(newOperation, PersistentState.OperationParameters);
            newRunner.Output += (S, verbosity) =>
            {
                // Output handler
                Dispatcher.Invoke(() =>
                {
                    // Prepend line numbers to each line of the output.
                    if (!string.IsNullOrEmpty(S))
                    {
                        AppLogger.Instance.Log(S, verbosity);
                    }
                });
            };

            Running = newRunner;
            try
            {
                Task task = newRunner.Run();
                OnPropertyChanged(nameof(IsRunningOperation));
                await task;
            }
            catch (Exception e)
            {
                AppLogger.Instance.Log(e.ToString(), LogVerbosity.Error);
            }

            OnPropertyChanged(nameof(IsRunningOperation));

            FlashWindow.Flash(Process.GetCurrentProcess().MainWindowHandle);
        }

        private void Terminate(object sender, RoutedEventArgs e)
        {
            if (Running != null)
            {
                _ = Running.Cancel();
            }
        }

        private void AddLogToOutputViewer(string line, LogVerbosity verbosity = LogVerbosity.Log)
        {
            LineCount++;
            string finalLine = "[" + $"{DateTime.Now:u}" + "][" + LineCount + @"]: " + line + "\r";

            TextRange tr = new(OutputTextBox.Document.ContentEnd, OutputTextBox.Document.ContentEnd);
            tr.Text = finalLine;

            Color color;

            switch (verbosity)
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
            if (IsRunningOperation)
            {
                MessageBoxResult result = MessageBox.Show("Process is running. Terminate it?", "Terminate process", MessageBoxButton.YesNoCancel);
                switch (result)
                {
                    case MessageBoxResult.Yes:
                        // Check is running again, because it may have finished while the message box was open
                        if (IsRunningOperation)
                        {
                            Running.Cancel().Wait();

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
            OpenFileDialog openFileDialog = new();
            if (openFileDialog.ShowDialog() == true)
            {
                string selectedPath = openFileDialog.FileName;
                PersistentData.Get().AddTarget(selectedPath);
            }
        }

        private void TargetGrid_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            FrameworkElement fe = e.Source as FrameworkElement;
            ContextMenu menu = new();
            fe.ContextMenu = menu;

            menu.Items.Add(new MenuItem { Header = "Open Directory", Command = new DelegateCommand(o => { RunProcess.OpenDirectory(SelectedTarget.TargetDirectory); }) });
            menu.Items.Add(new MenuItem { Header = "Open Output", Command = new DelegateCommand(o => { RunProcess.OpenDirectory(SelectedTarget.OutputDirectory); }) });

            menu.Items.Add(new Separator());

            if (SelectedTarget is Project)
            {
                Project project = SelectedTarget as Project;
                menu.Items.Add(new MenuItem { Header = "Open Staged Build", Command = new DelegateCommand(o => { RunProcess.OpenDirectory(project.GetStagedBuildWindowsPath()); }) });
                menu.Items.Add(new MenuItem { Header = "Open with Rider", Command = new DelegateCommand(o => { RunProcess.Run(Rider.FindExePath(), project.UProjectPath.AddQuotesIfContainsSpace()); }) });
            }

            if (SelectedTarget is Plugin)
            {
                Plugin plugin = SelectedTarget as Plugin;
                menu.Items.Add(new MenuItem { Header = $"Open {plugin.HostProject.DisplayName} with Rider", Command = new DelegateCommand(o => { RunProcess.Run(Rider.FindExePath(), plugin.HostProject.UProjectPath.AddQuotesIfContainsSpace()); }) });
            }

            menu.Items.Add(new Separator());

            menu.Items.Add(new MenuItem { Header = "Remove", Command = new DelegateCommand(o => { PersistentData.Get().RemoveTarget(SelectedTarget); }) });
        }
    }
}