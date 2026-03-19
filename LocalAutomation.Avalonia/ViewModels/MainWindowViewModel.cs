using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using LocalAutomation.Core;
using LocalAutomation.Extensions.Abstractions;
using Microsoft.Extensions.Logging;
using LocalAutomationApplicationHost = LocalAutomation.Application.LocalAutomationApplicationHost;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Drives the first real Avalonia shell by exposing target selection, operation selection, command preview, and a
/// generic option-editing surface through the shared LocalAutomation services.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private static readonly TimeSpan SessionSaveDebounceDelay = TimeSpan.FromMilliseconds(350);
    private const double MinimumOptionCardWidth = 340;
    private const double OptionCardSpacing = 12;
    private const int MaximumOptionColumns = 3;

    private readonly LocalAutomationApplicationHost _services;
    private readonly OperationParameters _operationParameters = new();
    private readonly DispatcherTimer _sessionSaveTimer;
    private bool _isHydratingSessionSelection;
    private bool _isRestoringSession;
    private bool _hasPendingSessionSave;
    private object? _currentOperation;
    private LocalAutomation.Core.ExecutionSession? _currentExecutionSession;
    private string _newTargetPath = string.Empty;
    private double _optionsCardWidth = MinimumOptionCardWidth;
    private int _optionsColumnCount = 1;
    private OperationDescriptor? _selectedOperation;
    private TargetListItemViewModel? _selectedTarget;
    private string _status = "Add a target path to begin using the LocalAutomation shell.";

    /// <summary>
    /// Creates the Avalonia main window view model around the shared LocalAutomation application host.
    /// </summary>
    public MainWindowViewModel(LocalAutomationApplicationHost services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _sessionSaveTimer = new DispatcherTimer { Interval = SessionSaveDebounceDelay };
        _sessionSaveTimer.Tick += HandleSessionSaveTimerTick;
        _operationParameters.PropertyChanged += HandleOperationParametersChanged;
        AttachApplicationLogStream();
        RestoreSessionState();
    }

    /// <summary>
    /// Gets the currently added targets shown in the shell.
    /// </summary>
    public ObservableCollection<TargetListItemViewModel> Targets { get; } = new();

    /// <summary>
    /// Gets the operations compatible with the current target selection.
    /// </summary>
    public ObservableCollection<OperationDescriptor> AvailableOperations { get; } = new();

    /// <summary>
    /// Gets the target actions currently available for the selected target.
    /// </summary>
    public ObservableCollection<TargetContextActionViewModel> TargetActions { get; } = new();

    /// <summary>
    /// Gets the editable option sets for the current target and operation selection.
    /// </summary>
    public ObservableCollection<OptionSetViewModel> EnabledOptionSets { get; } = new();

    /// <summary>
    /// Gets the current responsive options column count. The layout expands from one column up to three based on the
    /// available width in the options viewport.
    /// </summary>
    public int OptionsColumnCount
    {
        get => _optionsColumnCount;
        private set => SetProperty(ref _optionsColumnCount, value);
    }

    /// <summary>
    /// Gets the responsive option-card width used by the wrapping layout so columns fill the available width evenly
    /// without forcing all cards to match the tallest card's height.
    /// </summary>
    public double OptionsCardWidth
    {
        get => _optionsCardWidth;
        private set => SetProperty(ref _optionsCardWidth, value);
    }

    /// <summary>
    /// Gets the buffered execution log entries shown by the Avalonia shell.
    /// </summary>
    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = new();

    /// <summary>
    /// Gets or sets the path text entered into the Add Target input.
    /// </summary>
    public string NewTargetPath
    {
        get => _newTargetPath;
        set
        {
            if (SetProperty(ref _newTargetPath, value))
            {
                RaisePropertyChanged(nameof(CanAddTarget));
            }
        }
    }

    /// <summary>
    /// Gets or sets the currently selected target row.
    /// </summary>
    public TargetListItemViewModel? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (SetProperty(ref _selectedTarget, value))
            {
                _operationParameters.Target = value?.Target;
                if (!_isHydratingSessionSelection)
                {
                    RefreshOperationSelection();
                    RefreshTargetActions();
                    RaiseDerivedStateChanged();
                }

                SaveSessionState();
            }
        }
    }

    /// <summary>
    /// Gets or sets the currently selected operation descriptor.
    /// </summary>
    public OperationDescriptor? SelectedOperation
    {
        get => _selectedOperation;
        set
        {
            if (SetProperty(ref _selectedOperation, value))
            {
                _currentOperation = value != null
                    ? _services.OperationSession.CreateOperation(value.OperationType)
                    : null;

                if (!_isHydratingSessionSelection)
                {
                    RefreshEnabledOptionSets();
                    RaiseDerivedStateChanged();
                }

                SaveSessionState();
            }
        }
    }

    /// <summary>
    /// Gets or sets the additional arguments forwarded into the current operation parameters.
    /// </summary>
    public string AdditionalArguments
    {
        get => _operationParameters.AdditionalArguments;
        set
        {
            if (_operationParameters.AdditionalArguments != value)
            {
                _operationParameters.AdditionalArguments = value;
                RaisePropertyChanged();
            }
        }
    }

    /// <summary>
    /// Gets the current status message shown in the header area.
    /// </summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// Gets whether a non-empty target path can be added from the input box.
    /// </summary>
    public bool CanAddTarget => !string.IsNullOrWhiteSpace(NewTargetPath);

    /// <summary>
    /// Gets whether the current selection can produce a command preview string for copying.
    /// </summary>
    public bool CanCopyCommand => !string.IsNullOrWhiteSpace(_services.OperationSession.GetPrimaryCommandText(_currentOperation, _operationParameters));

    /// <summary>
    /// Gets whether the selected operation currently passes its execution requirements.
    /// </summary>
    public bool CanExecute => _services.OperationSession.CanExecute(_currentOperation, _operationParameters);

    /// <summary>
    /// Gets whether the current Avalonia execution session is running.
    /// </summary>
    public bool IsRunning => _currentExecutionSession is { IsRunning: true };

    /// <summary>
    /// Gets whether the execute warning panel should be shown.
    /// </summary>
    public bool ShowExecuteDisabledReason => !CanExecute;

    /// <summary>
    /// Gets the current execution blocking reason shown in the warning panel.
    /// </summary>
    public string ExecuteDisabledReason => _services.OperationSession.GetExecuteDisabledReason(_currentOperation, _operationParameters) ?? string.Empty;

    /// <summary>
    /// Gets the current command preview text.
    /// </summary>
    public string VisibleCommand => _services.OperationSession.GetVisibleCommandText(_currentOperation, _operationParameters);

    /// <summary>
    /// Gets the current operation button label.
    /// </summary>
    public string ExecuteButtonText => SelectedOperation?.DisplayName ?? "Execute";

    /// <summary>
    /// Gets a short execution summary line for the output panel.
    /// </summary>
    public string ExecutionSummary
    {
        get
        {
            if (_currentExecutionSession == null)
            {
                return "No execution has been started in the Avalonia shell yet.";
            }

            if (_currentExecutionSession.IsRunning)
            {
                return $"Running {_currentExecutionSession.OperationName} on {_currentExecutionSession.TargetName}";
            }

            if (_currentExecutionSession.Success == true)
            {
                return $"{_currentExecutionSession.OperationName} succeeded for {_currentExecutionSession.TargetName}";
            }

            return $"{_currentExecutionSession.OperationName} failed for {_currentExecutionSession.TargetName}";
        }
    }

    /// <summary>
    /// Gets the summary text for the currently selected target.
    /// </summary>
    public string SelectedTargetSummary => SelectedTarget != null
        ? $"{SelectedTarget.DisplayName} · {SelectedTarget.TypeName}"
        : "No target selected";

    /// <summary>
    /// Gets the selected target path for detail display.
    /// </summary>
    public string SelectedTargetPath => SelectedTarget?.TargetPath ?? "Choose a target to see its details.";

    /// <summary>
    /// Gets the selected target engine summary.
    /// </summary>
    public string SelectedTargetEngine => SelectedTarget?.EngineSummary ?? "Unknown engine";

    /// <summary>
    /// Gets whether the current target/operation selection supports selecting multiple engines.
    /// </summary>
    public bool OperationSupportsMultipleEngines => _services.OperationSession.SupportsMultipleEngines(_currentOperation);

    /// <summary>
    /// Gets the first command preview line for clipboard copy actions.
    /// </summary>
    public string? PrimaryCommandText => _services.OperationSession.GetPrimaryCommandText(_currentOperation, _operationParameters);

    /// <summary>
    /// Creates a target from the current path input and adds it to the in-memory session.
    /// </summary>
    public bool TryAddTargetFromInput(out string? errorMessage)
    {
        errorMessage = null;
        string source = NewTargetPath.Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            errorMessage = "Enter a project, plugin, package, or engine path first.";
            return false;
        }

        try
        {
            object createdTarget = _services.Targets.CreateTarget(source);
            if (createdTarget is not IOperationTarget target)
            {
                errorMessage = $"Created target '{createdTarget.GetType().Name}' does not implement IOperationTarget.";
                return false;
            }

            TargetListItemViewModel? existingTarget = Targets.FirstOrDefault(item =>
                string.Equals(item.Target.TargetPath, target.TargetPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.TypeName, target.TypeName, StringComparison.Ordinal));

            if (existingTarget != null)
            {
                SelectedTarget = existingTarget;
                Status = $"Selected existing {existingTarget.TypeName.ToLowerInvariant()} target '{existingTarget.DisplayName}'.";
                SaveSessionState();
                return true;
            }

            TargetListItemViewModel targetItem = new(target);
            Targets.Add(targetItem);
            SelectedTarget = targetItem;
            NewTargetPath = string.Empty;
            Status = $"Added {target.TypeName.ToLowerInvariant()} target '{target.DisplayName}'.";
            SaveSessionState();
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Removes the currently selected target from the in-memory session.
    /// </summary>
    public void RemoveSelectedTarget()
    {
        if (SelectedTarget == null)
        {
            return;
        }

        string removedTargetName = SelectedTarget.DisplayName;
        int removedIndex = Targets.IndexOf(SelectedTarget);
        Targets.Remove(SelectedTarget);

        if (Targets.Count == 0)
        {
            SelectedTarget = null;
            Status = $"Removed target '{removedTargetName}'. Add another target path to continue.";
            RefreshTargetActions();
            SaveSessionState();
            return;
        }

        int nextIndex = Math.Clamp(removedIndex, 0, Targets.Count - 1);
        SelectedTarget = Targets[nextIndex];
        Status = $"Removed target '{removedTargetName}'.";
        RefreshTargetActions();
        SaveSessionState();
    }

    /// <summary>
    /// Executes one of the currently available target actions.
    /// </summary>
    public void ExecuteTargetAction(TargetContextActionViewModel? action)
    {
        action?.Execute();
    }

    /// <summary>
    /// Flushes any queued session save immediately. Hosts should call this before shutdown so the most recent UI
    /// edits are not lost if the debounce window has not elapsed yet.
    /// </summary>
    public void FlushPendingSessionState()
    {
        if (!_hasPendingSessionSave)
        {
            return;
        }

        _sessionSaveTimer.Stop();
        SaveSessionStateCore();
    }

    /// <summary>
    /// Records a transient shell note in the shared log/output stream now that the window no longer reserves space for
    /// a dedicated status banner.
    /// </summary>
    public void SetStatus(string message)
    {
        Status = message;
        AppLogger.LoggerInstance.LogInformation(message);
    }

    /// <summary>
    /// Recomputes the number of option columns based on the current width of the options viewport so the cards fill
    /// the row evenly without leaving a large trailing gap.
    /// </summary>
    public void UpdateOptionsColumnCount(double availableWidth)
    {
        if (availableWidth <= 0)
        {
            OptionsColumnCount = 1;
            OptionsCardWidth = MinimumOptionCardWidth;
            return;
        }

        int computedColumns = (int)Math.Floor(availableWidth / MinimumOptionCardWidth);
        int clampedColumns = Math.Clamp(computedColumns, 1, MaximumOptionColumns);
        double totalSpacing = OptionCardSpacing * Math.Max(0, clampedColumns - 1);

        OptionsColumnCount = clampedColumns;
        OptionsCardWidth = Math.Max(MinimumOptionCardWidth, (availableWidth - totalSpacing) / clampedColumns);
    }

    /// <summary>
    /// Starts the current operation through the shared execution runtime and begins streaming logs into the shell.
    /// </summary>
    public void Execute()
    {
        if (_currentOperation == null)
        {
            SetStatus("Select an operation before executing.");
            return;
        }

        if (!CanExecute)
        {
            SetStatus(ExecuteDisabledReason);
            return;
        }

        if (IsRunning)
        {
            SetStatus("An operation is already running.");
            return;
        }

        LocalAutomation.Core.ExecutionSession session = _services.ExecutionRuntime.StartExecution(_currentOperation, _operationParameters);
        AttachExecutionSession(session);
        _services.Execution.SetCurrentSession(session);
        SetStatus($"Started {session.OperationName} for {session.TargetName}.");
    }

    /// <summary>
    /// Cancels the current execution session when one is running.
    /// </summary>
    public async Task CancelExecutionAsync()
    {
        if (_currentExecutionSession == null || !_currentExecutionSession.IsRunning)
        {
            return;
        }

        await _currentExecutionSession.CancelAsync();
        SetStatus($"Cancelling {_currentExecutionSession.OperationName}.");
        RaiseExecutionStateChanged();
    }

    /// <summary>
    /// Responds to parameter changes by refreshing the derived UI state that depends on operation parameters.
    /// </summary>
    private void HandleOperationParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Ignore nested parameter notifications while session hydration is still rebuilding target, operation, and
        // option state. Refreshing too early can treat the restored options as invalid and replace them with defaults
        // before the persisted operation selection is reapplied.
        if (_isRestoringSession)
        {
            return;
        }

        RefreshEnabledOptionSets();
        RaiseDerivedStateChanged();
        QueueSessionStateSave();
    }

    /// <summary>
    /// Seeds the output panel with application-level log entries and keeps listening so crashes and startup errors are
    /// visible even outside operation execution.
    /// </summary>
    private void AttachApplicationLogStream()
    {
        foreach (LogEntry entry in ApplicationLogService.LogStream.Entries)
        {
            LogEntries.Add(new LogEntryViewModel(entry.Message, entry.Verbosity));
        }

        ApplicationLogService.LogStream.EntryAdded += entry => Dispatcher.UIThread.Post(() =>
        {
            LogEntries.Add(new LogEntryViewModel(entry.Message, entry.Verbosity));
            RaiseExecutionStateChanged();
        });
    }

    /// <summary>
    /// Recomputes the target actions available for the selected target from the shared extension catalog.
    /// </summary>
    private void RefreshTargetActions()
    {
        TargetActions.Clear();

        if (SelectedTarget?.Target == null)
        {
            return;
        }

        foreach (ContextActionDescriptor descriptor in _services.ContextActions.GetActionsForTarget(SelectedTarget.Target))
        {
            object target = SelectedTarget.Target;
            TargetActions.Add(new TargetContextActionViewModel(descriptor, () => ExecuteTargetAction(descriptor, target)));
        }
    }

    /// <summary>
    /// Executes a selected target action and reports the result through the shared status text and application log.
    /// </summary>
    private void ExecuteTargetAction(ContextActionDescriptor descriptor, object target)
    {
        try
        {
            descriptor.Execute(target);
            SetStatus($"Executed '{descriptor.DisplayName}' for '{SelectedTarget?.DisplayName}'.");
        }
        catch (Exception ex)
        {
            AppLogger.LoggerInstance.LogError(ex, "Target action '{ActionName}' failed.", descriptor.DisplayName);
            SetStatus($"Failed to run '{descriptor.DisplayName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Restores the last persisted shell state so the Avalonia parity shell reopens with the previous targets,
    /// selection, and editable options.
    /// </summary>
    private void RestoreSessionState()
    {
        _isRestoringSession = true;
        try
        {
            SessionState state = SessionStateStore.Load();

            foreach (IOperationTarget target in state.Targets)
            {
                Targets.Add(new TargetListItemViewModel(target));
            }

            _operationParameters.AdditionalArguments = state.AdditionalArguments;
            foreach (OperationOptions options in state.OptionsInstances)
            {
                _operationParameters.OptionsInstances.Add(options);
            }

            NewTargetPath = state.NewTargetPath;

            TargetListItemViewModel? restoredSelection = Targets.FirstOrDefault(item =>
                string.Equals(item.Target.TargetPath, state.SelectedTargetPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Target.GetType().FullName, state.SelectedTargetTypeName, StringComparison.Ordinal));

            // Apply the restored target and operation together before recomputing enabled option sets so the
            // hydration pass does not temporarily coerce to a different operation and replace persisted values with
            // new defaults.
            _isHydratingSessionSelection = true;
            SelectedTarget = restoredSelection ?? Targets.FirstOrDefault();
            RefreshOperationSelection();

            if (state.OperationType != null)
            {
                SelectedOperation = AvailableOperations.FirstOrDefault(descriptor => descriptor.OperationType == state.OperationType) ?? SelectedOperation;
            }

            _isHydratingSessionSelection = false;
            RefreshTargetActions();
            RefreshEnabledOptionSets();
            RaiseDerivedStateChanged();

            if (SelectedTarget != null)
            {
                Status = $"Restored {Targets.Count} target(s) from the previous Avalonia session.";
            }
        }
        finally
        {
            _isHydratingSessionSelection = false;
            _isRestoringSession = false;
            RaiseDerivedStateChanged();
        }
    }

    /// <summary>
    /// Persists the current shell state after user-driven selection or option changes.
    /// </summary>
    private void SaveSessionState()
    {
        if (_isRestoringSession)
        {
            return;
        }

        _hasPendingSessionSave = true;
        _sessionSaveTimer.Stop();
        _sessionSaveTimer.Start();
    }

    /// <summary>
    /// Writes the latest session snapshot to disk immediately.
    /// </summary>
    private void SaveSessionStateCore()
    {
        _hasPendingSessionSave = false;

        SessionStateStore.Save(new SessionState
        {
            Targets = new ObservableCollection<IOperationTarget>(Targets.Select(item => item.Target)),
            AdditionalArguments = _operationParameters.AdditionalArguments,
            OptionsInstances = _operationParameters.OptionsInstances.ToList(),
            OperationType = SelectedOperation?.OperationType,
            NewTargetPath = NewTargetPath,
            SelectedTargetPath = SelectedTarget?.Target.TargetPath,
            SelectedTargetTypeName = SelectedTarget?.Target.GetType().FullName
        });
    }

    /// <summary>
    /// Commits a debounced session save once the user has paused edits for a short interval.
    /// </summary>
    private void HandleSessionSaveTimerTick(object? sender, EventArgs e)
    {
        _sessionSaveTimer.Stop();

        if (_isRestoringSession || !_hasPendingSessionSave)
        {
            return;
        }

        SaveSessionStateCore();
    }

    /// <summary>
    /// Queues a debounced session save for the current shell state.
    /// </summary>
    private void QueueSessionStateSave()
    {
        SaveSessionState();
    }

    /// <summary>
    /// Attaches the shared execution session to the shell and mirrors its log/output state into Avalonia-friendly
    /// observable collections.
    /// </summary>
    private void AttachExecutionSession(LocalAutomation.Core.ExecutionSession session)
    {
        _currentExecutionSession = session;

        session.LogStream.EntryAdded += entry => Dispatcher.UIThread.Post(() =>
        {
            LogEntries.Add(new LogEntryViewModel(entry.Message, entry.Verbosity));
            RaiseExecutionStateChanged();
        });

        _ = WatchExecutionCompletionAsync(session);
        RaiseExecutionStateChanged();
    }

    /// <summary>
    /// Polls the running session until it completes so completion state can be reflected back into the shell.
    /// </summary>
    private async Task WatchExecutionCompletionAsync(LocalAutomation.Core.ExecutionSession session)
    {
        while (session.IsRunning)
        {
            await Task.Delay(100);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            RaiseExecutionStateChanged();

            if (session.Success == true)
            {
                SetStatus($"{session.OperationName} succeeded for {session.TargetName}.");
            }
            else
            {
                SetStatus($"{session.OperationName} finished with failure for {session.TargetName}.");
            }
        });
    }

    /// <summary>
    /// Rebuilds the compatible operation list for the currently selected target and preserves the active selection
    /// when it remains valid.
    /// </summary>
    private void RefreshOperationSelection()
    {
        AvailableOperations.Clear();
        foreach (OperationDescriptor descriptor in _services.Operations.GetAvailableOperations(SelectedTarget?.Target))
        {
            AvailableOperations.Add(descriptor);
        }

        Type? selectedOperationType = SelectedOperation?.OperationType;
        Type? coercedOperationType = _services.OperationSession.CoerceSelectedOperationType(SelectedTarget?.Target, selectedOperationType);
        SelectedOperation = coercedOperationType == null
            ? null
            : AvailableOperations.FirstOrDefault(descriptor => descriptor.OperationType == coercedOperationType);
    }

    /// <summary>
    /// Rebuilds the editable option set list for the currently selected operation and target.
    /// </summary>
    private void RefreshEnabledOptionSets()
    {
        var enabledOptionTypes = _services.OperationSession.GetEnabledOptionSetTypes(_currentOperation, SelectedTarget?.Target).ToList();

        if (!enabledOptionTypes.Contains(typeof(UnrealAutomationCommon.Operations.OperationOptionTypes.AdditionalArgumentsOptions)))
        {
            enabledOptionTypes.Add(typeof(UnrealAutomationCommon.Operations.OperationOptionTypes.AdditionalArgumentsOptions));
        }

        foreach (Type optionSetType in enabledOptionTypes)
        {
            _operationParameters.EnsureOptionsInstance(optionSetType);
        }

        foreach (OperationOptions options in _operationParameters.OptionsInstances.ToList())
        {
            if (!enabledOptionTypes.Contains(options.GetType()))
            {
                _operationParameters.RemoveOptionsInstance(options.GetType());
            }
        }

        EnabledOptionSets.Clear();
        foreach (OperationOptions options in _operationParameters.OptionsInstances)
        {
            EnabledOptionSets.Add(new OptionSetViewModel(options, _services.OptionEditors.GetEditorTarget(options)));
        }
    }

    /// <summary>
    /// Raises change notifications for all derived state displayed by the shell.
    /// </summary>
    private void RaiseDerivedStateChanged()
    {
        RaisePropertyChanged(nameof(AdditionalArguments));
        RaisePropertyChanged(nameof(CanAddTarget));
        RaisePropertyChanged(nameof(CanCopyCommand));
        RaisePropertyChanged(nameof(CanExecute));
        RaisePropertyChanged(nameof(ExecuteButtonText));
        RaisePropertyChanged(nameof(ExecuteDisabledReason));
        RaisePropertyChanged(nameof(OperationSupportsMultipleEngines));
        RaisePropertyChanged(nameof(PrimaryCommandText));
        RaisePropertyChanged(nameof(ShowExecuteDisabledReason));
        RaisePropertyChanged(nameof(SelectedTargetEngine));
        RaisePropertyChanged(nameof(SelectedTargetPath));
        RaisePropertyChanged(nameof(SelectedTargetSummary));
        RaisePropertyChanged(nameof(VisibleCommand));
        RaiseExecutionStateChanged();
    }

    /// <summary>
    /// Raises change notifications for all execution-related shell state.
    /// </summary>
    private void RaiseExecutionStateChanged()
    {
        RaisePropertyChanged(nameof(CanExecute));
        RaisePropertyChanged(nameof(ExecutionSummary));
        RaisePropertyChanged(nameof(IsRunning));
    }
}
