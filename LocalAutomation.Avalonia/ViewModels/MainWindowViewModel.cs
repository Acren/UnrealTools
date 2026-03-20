using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using LocalAutomation.Core;
using LocalAutomation.Extensions.Abstractions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
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

    private readonly LocalAutomationApplicationHost _services;
    private readonly OperationParameters _operationParameters = new();
    private readonly SessionPersistenceService _sessionPersistence;
    private readonly DispatcherTimer _sessionSaveTimer;
    private readonly TargetPickerItemViewModel _addTargetPickerItem = TargetPickerItemViewModel.CreateAddAction();
    private bool _isApplyingTargetState;
    private bool _isHydratingSessionSelection;
    private bool _isRestoringSession;
    private bool _hasPendingSessionSave;
    private object? _currentOperation;
    private string _newTargetPath = string.Empty;
    private OperationDescriptor? _selectedOperation;
    private RuntimeTaskTabViewModel? _selectedRuntimeTab;
    private SessionSnapshot _sessionSnapshot = new();
    private TargetPickerItemViewModel? _selectedTargetPickerItem;
    private TargetListItemViewModel? _selectedTarget;
    private string _status = "Add a target path to begin using the LocalAutomation shell.";

    /// <summary>
    /// Creates the Avalonia main window view model around the shared LocalAutomation application host.
    /// </summary>
    public MainWindowViewModel(LocalAutomationApplicationHost services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _sessionPersistence = new SessionPersistenceService(services);
        _sessionSaveTimer = new DispatcherTimer { Interval = SessionSaveDebounceDelay };
        _sessionSaveTimer.Tick += HandleSessionSaveTimerTick;
        _operationParameters.PropertyChanged += HandleOperationParametersChanged;
        TargetPickerItems.Add(_addTargetPickerItem);
        RuntimeTabs.Add(CreateApplicationLogTab());
        SelectedRuntimeTab = RuntimeTabs[0];
        AttachApplicationLogStream();
        RestoreSessionState();
    }

    /// <summary>
    /// Gets the currently added targets shown in the shell.
    /// </summary>
    public ObservableCollection<TargetListItemViewModel> Targets { get; } = new();

    /// <summary>
    /// Gets the target-picker rows, including the synthetic add-target action shown at the bottom of the dropdown.
    /// </summary>
    public ObservableCollection<TargetPickerItemViewModel> TargetPickerItems { get; } = new();

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
    /// Gets the runtime tabs shown in the global runtime panel. The first tab is reserved for application logs.
    /// </summary>
    public ObservableCollection<RuntimeTaskTabViewModel> RuntimeTabs { get; } = new();

    /// <summary>
    /// Gets or sets the currently selected runtime tab.
    /// </summary>
    public RuntimeTaskTabViewModel? SelectedRuntimeTab
    {
        get => _selectedRuntimeTab;
        set
        {
            if (SetProperty(ref _selectedRuntimeTab, value))
            {
                foreach (RuntimeTaskTabViewModel tab in RuntimeTabs)
                {
                    tab.IsSelected = ReferenceEquals(tab, value);
                }

                RaiseExecutionStateChanged();
            }
        }
    }

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
    /// Gets or sets the picker row currently shown by the target dropdown.
    /// </summary>
    public TargetPickerItemViewModel? SelectedTargetPickerItem
    {
        get => _selectedTargetPickerItem;
        set
        {
            // The synthetic add-target row is handled by the view, so keep the last real target selected in the view
            // model while still allowing the popup to surface the action entry.
            if (value?.IsAddAction == true)
            {
                RaisePropertyChanged();
                return;
            }

            if (SetProperty(ref _selectedTargetPickerItem, value) && !ReferenceEquals(SelectedTarget, value?.TargetItem))
            {
                SelectedTarget = value?.TargetItem;
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
            IOperationTarget? previousTarget = _selectedTarget?.Target;
            if (!_isHydratingSessionSelection && !_isRestoringSession && !_isApplyingTargetState && previousTarget != null)
            {
                CaptureTargetState(previousTarget);
            }

            if (SetProperty(ref _selectedTarget, value))
            {
                _operationParameters.Target = value?.Target;
                SyncSelectedTargetPickerItem();
                if (!_isHydratingSessionSelection)
                {
                    RefreshOperationSelection();
                    RestoreSelectedTargetState();
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

                if (!_isHydratingSessionSelection && !_isApplyingTargetState)
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
    public bool IsRunning => SelectedRuntimeTab?.CanTerminate == true;

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
            if (SelectedRuntimeTab == null)
            {
                return "No runtime tab is selected.";
            }

            if (SelectedRuntimeTab.IsApplicationLog)
            {
                return "Application log messages appear here even when no tasks are running.";
            }

            if (SelectedRuntimeTab.Session?.IsRunning == true)
            {
                return $"Running {SelectedRuntimeTab.Session.OperationName} on {SelectedRuntimeTab.Session.TargetName}";
            }

            if (SelectedRuntimeTab.Session?.Success == true)
            {
                return $"{SelectedRuntimeTab.Session.OperationName} succeeded for {SelectedRuntimeTab.Session.TargetName}";
            }

            if (SelectedRuntimeTab.Session != null)
            {
                return $"{SelectedRuntimeTab.Session.OperationName} failed for {SelectedRuntimeTab.Session.TargetName}";
            }

            return SelectedRuntimeTab.Subtitle;
        }
    }

    /// <summary>
    /// Gets the log entries shown for the currently selected runtime tab.
    /// </summary>
    public ObservableCollection<LogEntryViewModel> SelectedRuntimeLogEntries => SelectedRuntimeTab?.LogEntries ?? new ObservableCollection<LogEntryViewModel>();

    /// <summary>
    /// Gets the runtime tab title shown in the panel header when a tab is selected.
    /// </summary>
    public string SelectedRuntimeTabTitle => SelectedRuntimeTab?.Title ?? "Runtime";

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
            AddTargetItem(targetItem);
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
        RemoveTargetItem(SelectedTarget);

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

        LocalAutomation.Core.ExecutionSession session = _services.ExecutionRuntime.StartExecution(_currentOperation, _operationParameters);
        AttachExecutionSession(session);
        _services.Execution.AddSession(session);
        SetStatus($"Started {session.OperationName} for {session.TargetName}.");
    }

    /// <summary>
    /// Cancels the current execution session when one is running.
    /// </summary>
    public async Task CancelExecutionAsync()
    {
        if (SelectedRuntimeTab?.Session is not { IsRunning: true } session)
        {
            return;
        }

        await session.CancelAsync();
        SetStatus($"Cancelling {session.OperationName}.");
        RaiseExecutionStateChanged();
    }

    /// <summary>
    /// Responds to parameter changes by refreshing only derived shell state that depends on live parameter values.
    /// The enabled option-set list depends on the selected operation and target, not on edits within an existing
    /// option card, so rebuilding `EnabledOptionSets` here would recreate the property-grid editors on every keystroke
    /// and drop focus from the currently edited control.
    /// </summary>
    private void HandleOperationParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Ignore nested parameter notifications while session hydration is still rebuilding target, operation, and
        // option state. Refreshing too early can treat the restored options as invalid and replace them with defaults
        // before the persisted operation selection is reapplied.
        if (_isRestoringSession || _isApplyingTargetState)
        {
            return;
        }

        RaiseDerivedStateChanged();
        QueueSessionStateSave();
    }

    /// <summary>
    /// Seeds the output panel with application-level log entries and keeps listening so crashes and startup errors are
    /// visible even outside operation execution.
    /// </summary>
    private void AttachApplicationLogStream()
    {
        RuntimeTaskTabViewModel applicationTab = RuntimeTabs[0];
        foreach (LogEntry entry in ApplicationLogService.LogStream.Entries)
        {
            applicationTab.AddLogEntry(new LogEntryViewModel(entry.Message, entry.Verbosity));
        }

        ApplicationLogService.LogStream.EntryAdded += entry => Dispatcher.UIThread.Post(() =>
        {
            applicationTab.AddLogEntry(new LogEntryViewModel(entry.Message, entry.Verbosity));
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
    /// Creates the permanent first runtime tab used for application-level logs.
    /// </summary>
    private static RuntimeTaskTabViewModel CreateApplicationLogTab()
    {
        return new RuntimeTaskTabViewModel(
            id: "application-log",
            title: "Output",
            subtitle: "Application log messages and shell diagnostics.",
            isApplicationLog: true);
    }

    /// <summary>
    /// Returns the persisted snapshot for the provided runtime target when one exists.
    /// </summary>
    private TargetSessionSnapshot? FindTargetSnapshot(IOperationTarget target)
    {
        string key = _sessionPersistence.CreateTargetSnapshot(target).Key;
        return _sessionSnapshot.Targets.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
    }

    /// <summary>
    /// Captures the current operation and option values into the nested persisted state for the provided target.
    /// </summary>
    private void CaptureTargetState(IOperationTarget? target)
    {
        if (target == null)
        {
            return;
        }

        TargetSessionSnapshot snapshot = FindTargetSnapshot(target) ?? _sessionPersistence.CreateTargetSnapshot(target);
        snapshot.State.SelectedOperationId = SelectedOperation?.Id;
        snapshot.State.OptionValues = _services.OptionValues.Capture(_operationParameters.OptionsInstances.Cast<object>());

        int existingIndex = _sessionSnapshot.Targets.FindIndex(item => string.Equals(item.Key, snapshot.Key, StringComparison.Ordinal));
        if (existingIndex >= 0)
        {
            _sessionSnapshot.Targets[existingIndex] = snapshot;
        }
        else
        {
            _sessionSnapshot.Targets.Add(snapshot);
        }
    }

    /// <summary>
    /// Restores the selected target's persisted operation and option values into the live editing state.
    /// </summary>
    private void RestoreSelectedTargetState()
    {
        _isApplyingTargetState = true;
        try
        {
            _operationParameters.ResetOptions();
            _operationParameters.AdditionalArguments = string.Empty;

            TargetSessionSnapshot? targetSnapshot = SelectedTarget?.Target == null ? null : FindTargetSnapshot(SelectedTarget.Target);
            if (targetSnapshot?.State.SelectedOperationId != null)
            {
                SelectedOperation = AvailableOperations.FirstOrDefault(descriptor => string.Equals(descriptor.Id, targetSnapshot.State.SelectedOperationId, StringComparison.Ordinal)) ?? SelectedOperation;
            }

            RefreshEnabledOptionSets();

            if (targetSnapshot != null)
            {
                _services.OptionValues.Apply(_operationParameters.OptionsInstances.Cast<object>(), targetSnapshot.State.OptionValues);
            }

            // Recreate property-grid card targets after applying restored values so adapter-backed editors (such as
            // engine version and insights checklists) reflect the rehydrated state instead of the pre-restore values
            // captured when the cards were first created.
            RebuildOptionCards();
        }
        finally
        {
            _isApplyingTargetState = false;
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
            _sessionSnapshot = _sessionPersistence.Load();
            List<TargetSessionSnapshot> restoredSnapshots = new();

            foreach (TargetSessionSnapshot targetSnapshot in _sessionSnapshot.Targets)
            {
                if (_sessionPersistence.TryRestoreTarget(targetSnapshot, out IOperationTarget? target) && target != null)
                {
                    AddTargetItem(new TargetListItemViewModel(target));
                    restoredSnapshots.Add(targetSnapshot);
                }
            }

            // Keep only snapshots that successfully restored so nested per-target state survives startup; do not
            // replace them with fresh empty snapshots before the restore pass can apply option values.
            _sessionSnapshot.Targets = restoredSnapshots;
            NewTargetPath = _sessionSnapshot.PendingTargetPath;

            TargetListItemViewModel? restoredSelection = Targets.FirstOrDefault(item =>
                string.Equals(_sessionPersistence.CreateTargetSnapshot(item.Target).Key, _sessionSnapshot.SelectedTargetKey, StringComparison.Ordinal));

            // Apply the restored target and operation together before recomputing enabled option sets so the
            // hydration pass does not temporarily coerce to a different operation and replace persisted values with
            // new defaults.
            _isHydratingSessionSelection = true;
            SelectedTarget = restoredSelection ?? Targets.FirstOrDefault();
            RefreshOperationSelection();

            _isHydratingSessionSelection = false;
            RestoreSelectedTargetState();
            RefreshTargetActions();
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
        if (_isRestoringSession || _isApplyingTargetState)
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

        CaptureTargetState(SelectedTarget?.Target);
        _sessionSnapshot.Targets = Targets.Select(item =>
        {
            TargetSessionSnapshot snapshot = FindTargetSnapshot(item.Target) ?? _sessionPersistence.CreateTargetSnapshot(item.Target);
            snapshot.TargetTypeId = _services.Targets.GetTargetTypeId(item.Target) ?? snapshot.TargetTypeId;
            snapshot.Path = item.Target.TargetPath;
            snapshot.Key = _sessionPersistence.BuildTargetKey(snapshot.TargetTypeId, snapshot.Path);
            snapshot.State ??= new TargetUiStateSnapshot();
            return snapshot;
        }).ToList();
        _sessionSnapshot.SelectedTargetKey = SelectedTarget?.Target == null ? null : _sessionPersistence.CreateTargetSnapshot(SelectedTarget.Target).Key;
        _sessionSnapshot.PendingTargetPath = NewTargetPath;

        _sessionPersistence.Save(_sessionSnapshot);
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
        RuntimeTaskTabViewModel runtimeTab = new(
            id: session.Id,
            title: $"{session.TargetName} - {session.OperationName}",
            subtitle: $"{session.OperationName} on {session.TargetName}",
            isApplicationLog: false,
            session: session);

        RuntimeTabs.Add(runtimeTab);
        SelectedRuntimeTab = runtimeTab;

        session.LogStream.EntryAdded += entry => Dispatcher.UIThread.Post(() =>
        {
            runtimeTab.AddLogEntry(new LogEntryViewModel(entry.Message, entry.Verbosity));
            RaiseExecutionStateChanged();
        });

        _ = WatchExecutionCompletionAsync(runtimeTab);
        RaiseExecutionStateChanged();
    }

    /// <summary>
    /// Polls the running session until it completes so completion state can be reflected back into the shell.
    /// </summary>
    private async Task WatchExecutionCompletionAsync(RuntimeTaskTabViewModel runtimeTab)
    {
        LocalAutomation.Core.ExecutionSession session = runtimeTab.Session!;
        while (session.IsRunning)
        {
            await Task.Delay(100);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            session.FinishedAt = DateTimeOffset.Now;
            runtimeTab.NotifyStateChanged();
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
    /// Rebuilds the option card view models from the current live option instances without recomputing which option
    /// sets are enabled.
    /// </summary>
    private void RebuildOptionCards()
    {
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
    /// Adds a real target row to both the session-backed target list and the picker list while keeping the synthetic
    /// add-target action anchored at the bottom of the dropdown.
    /// </summary>
    private void AddTargetItem(TargetListItemViewModel targetItem)
    {
        Targets.Add(targetItem);
        TargetPickerItems.Insert(Math.Max(0, TargetPickerItems.Count - 1), new TargetPickerItemViewModel(targetItem));
    }

    /// <summary>
    /// Removes a real target row from both the working target list and the dropdown picker entries.
    /// </summary>
    private void RemoveTargetItem(TargetListItemViewModel targetItem)
    {
        Targets.Remove(targetItem);

        TargetPickerItemViewModel? pickerItem = TargetPickerItems.FirstOrDefault(item => ReferenceEquals(item.TargetItem, targetItem));
        if (pickerItem != null)
        {
            TargetPickerItems.Remove(pickerItem);
        }
    }

    /// <summary>
    /// Mirrors the real selected target into the dropdown selection so the collapsed picker always shows the active
    /// target instead of the synthetic add row.
    /// </summary>
    private void SyncSelectedTargetPickerItem()
    {
        TargetPickerItemViewModel? pickerItem = SelectedTarget == null
            ? null
            : TargetPickerItems.FirstOrDefault(item => ReferenceEquals(item.TargetItem, SelectedTarget));

        SetProperty(ref _selectedTargetPickerItem, pickerItem, nameof(SelectedTargetPickerItem));
    }

    /// <summary>
    /// Raises change notifications for all execution-related shell state.
    /// </summary>
    private void RaiseExecutionStateChanged()
    {
        RaisePropertyChanged(nameof(CanExecute));
        RaisePropertyChanged(nameof(ExecutionSummary));
        RaisePropertyChanged(nameof(IsRunning));
        RaisePropertyChanged(nameof(SelectedRuntimeLogEntries));
        RaisePropertyChanged(nameof(SelectedRuntimeTabTitle));
    }
}
