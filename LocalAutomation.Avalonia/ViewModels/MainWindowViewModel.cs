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
    private bool _isApplyingTargetState;
    private bool _isHydratingSessionSelection;
    private bool _isRestoringSession;
    private bool _hasPendingSessionSave;
    private object? _currentOperation;
    private OperationDescriptor? _selectedOperation;
    private SessionSnapshot _sessionSnapshot = new();
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
        Target = new TargetPanelViewModel(services, SetStatus, HandleSelectedTargetChanged, SaveSessionState);
        Runtime = new RuntimePanelViewModel(services, SetStatus);
        RestoreSessionState();
    }

    /// <summary>
    /// Gets the target panel view model that owns target-picker state and target-scoped actions.
    /// </summary>
    public TargetPanelViewModel Target { get; }

    /// <summary>
    /// Gets the operations compatible with the current target selection.
    /// </summary>
    public ObservableCollection<OperationDescriptor> AvailableOperations { get; } = new();

    /// <summary>
    /// Gets the editable option sets for the current target and operation selection.
    /// </summary>
    public ObservableCollection<OptionSetViewModel> EnabledOptionSets { get; } = new();

    /// <summary>
    /// Gets the runtime panel view model that owns task tabs, selected-task metrics, and runtime actions.
    /// </summary>
    public RuntimePanelViewModel Runtime { get; }

    /// <summary>
    /// Gets the currently selected target row from the target panel.
    /// </summary>
    public TargetListItemViewModel? SelectedTarget => Target.SelectedTarget;

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
    /// Gets whether the current selection can produce a command preview string for copying.
    /// </summary>
    public bool CanCopyCommand => !string.IsNullOrWhiteSpace(_services.OperationSession.GetPrimaryCommandText(_currentOperation, _operationParameters));

    /// <summary>
    /// Gets whether the selected operation currently passes its execution requirements.
    /// </summary>
    public bool CanExecute => _services.OperationSession.CanExecute(_currentOperation, _operationParameters);

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
    /// Gets whether the current target/operation selection supports selecting multiple engines.
    /// </summary>
    public bool OperationSupportsMultipleEngines => _services.OperationSession.SupportsMultipleEngines(_currentOperation);

    /// <summary>
    /// Gets the first command preview line for clipboard copy actions.
    /// </summary>
    public string? PrimaryCommandText => _services.OperationSession.GetPrimaryCommandText(_currentOperation, _operationParameters);

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
        Runtime.AttachExecutionSession(session);
        _services.Execution.AddSession(session);
        SetStatus($"Started {session.OperationName} for {session.TargetName}.");
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
                    Target.AddTargetItem(new TargetListItemViewModel(target));
                    restoredSnapshots.Add(targetSnapshot);
                }
            }

            // Keep only snapshots that successfully restored so nested per-target state survives startup; do not
            // replace them with fresh empty snapshots before the restore pass can apply option values.
            _sessionSnapshot.Targets = restoredSnapshots;
            Target.NewTargetPath = _sessionSnapshot.PendingTargetPath;

            TargetListItemViewModel? restoredSelection = Target.Targets.FirstOrDefault(item =>
                string.Equals(_sessionPersistence.CreateTargetSnapshot(item.Target).Key, _sessionSnapshot.SelectedTargetKey, StringComparison.Ordinal));

            // Apply the restored target and operation together before recomputing enabled option sets so the
            // hydration pass does not temporarily coerce to a different operation and replace persisted values with
            // new defaults.
            _isHydratingSessionSelection = true;
            Target.SelectedTarget = restoredSelection ?? Target.Targets.FirstOrDefault();
            RefreshOperationSelection();

            _isHydratingSessionSelection = false;
            RestoreSelectedTargetState();
            RaiseDerivedStateChanged();

            if (Target.SelectedTarget != null)
            {
                Status = $"Restored {Target.Targets.Count} target(s) from the previous Avalonia session.";
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

        CaptureTargetState(Target.SelectedTarget?.Target);
        _sessionSnapshot.Targets = Target.Targets.Select(item =>
        {
            TargetSessionSnapshot snapshot = FindTargetSnapshot(item.Target) ?? _sessionPersistence.CreateTargetSnapshot(item.Target);
            snapshot.TargetTypeId = _services.Targets.GetTargetTypeId(item.Target) ?? snapshot.TargetTypeId;
            snapshot.Path = item.Target.TargetPath;
            snapshot.Key = _sessionPersistence.BuildTargetKey(snapshot.TargetTypeId, snapshot.Path);
            snapshot.State ??= new TargetUiStateSnapshot();
            return snapshot;
        }).ToList();
        _sessionSnapshot.SelectedTargetKey = Target.SelectedTarget?.Target == null ? null : _sessionPersistence.CreateTargetSnapshot(Target.SelectedTarget.Target).Key;
        _sessionSnapshot.PendingTargetPath = Target.NewTargetPath;

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
    /// Applies shell-level side effects after the target panel changes the selected target.
    /// </summary>
    private void HandleSelectedTargetChanged(TargetListItemViewModel? previousTarget, TargetListItemViewModel? selectedTarget)
    {
        IOperationTarget? previousOperationTarget = previousTarget?.Target;
        if (!_isHydratingSessionSelection && !_isRestoringSession && !_isApplyingTargetState && previousOperationTarget != null)
        {
            CaptureTargetState(previousOperationTarget);
        }

        _operationParameters.Target = selectedTarget?.Target;
        if (!_isHydratingSessionSelection)
        {
            RefreshOperationSelection();
            RestoreSelectedTargetState();
            RaiseDerivedStateChanged();
        }

        SaveSessionState();
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
        RaisePropertyChanged(nameof(CanCopyCommand));
        RaisePropertyChanged(nameof(CanExecute));
        RaisePropertyChanged(nameof(ExecuteButtonText));
        RaisePropertyChanged(nameof(ExecuteDisabledReason));
        RaisePropertyChanged(nameof(OperationSupportsMultipleEngines));
        RaisePropertyChanged(nameof(PrimaryCommandText));
        RaisePropertyChanged(nameof(ShowExecuteDisabledReason));
        RaisePropertyChanged(nameof(VisibleCommand));
    }
}
