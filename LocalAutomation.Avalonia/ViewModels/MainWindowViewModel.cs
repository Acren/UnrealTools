using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Threading;
using LocalAutomation.Application;
using LocalAutomation.Core;
using LocalAutomation.Extensions.Abstractions;
using LocalAutomation.Runtime;
using LocalAutomationApplicationHost = LocalAutomation.Application.LocalAutomationApplicationHost;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Drives the Avalonia shell by exposing target selection, operation selection, command preview, and generic option
/// editing through the shared LocalAutomation services instead of extension-specific runtime types.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    [Flags]
    private enum SelectionTransitionState
    {
        None = 0,
        ApplyingTargetState = 1,
        HydratingSessionSelection = 2,
        RestoringSession = 4,
        ApplyingOperationState = 8
    }

    private static readonly TimeSpan SessionSaveDebounceDelay = TimeSpan.FromMilliseconds(350);

    private readonly LocalAutomationApplicationHost _services;
    private readonly OperationParameterSession _parameterSession;
    private readonly SessionPersistenceService _sessionPersistence;
    private readonly DebouncedBackgroundSaver<SessionSnapshot> _sessionSnapshotSaver;
    private readonly DebouncedBackgroundSaver<PersistedSettingsWriteBatch> _targetOptionValuesSaver;
    private ObservableCollection<OperationDescriptor> _availableOperations = new();
    private Operation? _currentOperation;
    private OperationId? _selectedOperationId;
    private SessionSnapshot _sessionSnapshot = new();
    private string _status = $"Add a target path to begin using the {App.ShellIdentity.ApplicationName} shell.";
    private SelectionTransitionState _selectionTransitionState;
    private bool _operationParametersRefreshQueued;
    private bool _disposed;

    /// <summary>
    /// Creates the Avalonia main-window view model around the shared LocalAutomation application host.
    /// </summary>
    public MainWindowViewModel(LocalAutomationApplicationHost services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _parameterSession = _services.OperationRuntime.CreateParameterSession();
        _sessionPersistence = new SessionPersistenceService(services);
        _sessionSnapshotSaver = new DebouncedBackgroundSaver<SessionSnapshot>(
            debounceDelay: SessionSaveDebounceDelay,
            saveState: _sessionPersistence.Save,
            handleSaveException: HandleSessionSaveException);
        _targetOptionValuesSaver = new DebouncedBackgroundSaver<PersistedSettingsWriteBatch>(
            debounceDelay: SessionSaveDebounceDelay,
            saveState: _services.OptionValues.SaveCapturedSettings,
            mergeStates: static (earlier, later) => earlier.Merge(later),
            handleSaveException: HandleTargetSettingsSaveException);
        _parameterSession.PropertyChanged += HandleOperationParametersChanged;
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
    public ObservableCollection<OperationDescriptor> AvailableOperations
    {
        get => _availableOperations;
        private set => SetProperty(ref _availableOperations, value);
    }

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
    /// Gets the currently selected operation descriptor resolved from the current compatible operation list.
    /// </summary>
    public OperationDescriptor? SelectedOperation
    {
        get => ResolveOperationById(AvailableOperations, _selectedOperationId);
        set => SelectedOperationId = value?.Id;
    }

    /// <summary>
    /// Gets or sets the stable identifier of the currently selected operation so the picker can preserve selection
    /// across operation-list refreshes without depending on object identity.
    /// </summary>
    public OperationId? SelectedOperationId
    {
        get => _selectedOperationId;
        set
        {
            OperationId? normalizedOperationId = ResolveAvailableOperationById(value)?.Id;
            if (_selectedOperationId == normalizedOperationId)
            {
                return;
            }

            // Resolve the requested identifier against the currently compatible operations so user-driven picker
            // changes always activate a runtime operation that the selected target can actually execute.
            ApplySelectedOperationSelection(normalizedOperationId);
        }
    }

    /// <summary>
    /// Applies a selected operation identifier to both the view-model state and the runtime parameter session.
    /// </summary>
    private void ApplySelectedOperationSelection(OperationId? selectedOperationId)
    {
        Operation? previousOperation = _currentOperation;
        OperationParameters previousParameters = _parameterSession.RawValue;
        OperationId? previousSelectedOperationId = _selectedOperationId;
        OperationDescriptor? selectedOperation = ResolveAvailableOperationById(selectedOperationId);

        if (previousSelectedOperationId == selectedOperationId)
        {
            return;
        }

        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("OperationSwitch");
        PerformanceTelemetry.SetTag(activity, "operation.id", selectedOperationId?.Value ?? string.Empty);
        PerformanceTelemetry.SetTag(activity, "operation.name", selectedOperation?.DisplayName ?? string.Empty);
        PerformanceTelemetry.SetTag(activity, "previous_operation.id", previousSelectedOperationId?.Value ?? string.Empty);
        PerformanceTelemetry.SetTag(activity, "target.type", GetSelectedTargetTypeId()?.Value ?? string.Empty);
        PerformanceTelemetry.SetTag(activity, "available_operation.count", AvailableOperations.Count);

        // Capture the current target-scoped option values before changing the selected operation because operation
        // switches can temporarily remove option sets like compiler settings from the live parameter model.
        if (!IsSelectionTransitionActive(SelectionTransitionState.HydratingSessionSelection | SelectionTransitionState.ApplyingTargetState | SelectionTransitionState.RestoringSession) && SelectedTarget?.Target != null)
        {
            QueueTargetOptionValuePersistence(SelectedTarget.Target);
            RememberOperationForTargetType(SelectedTarget.Target, previousSelectedOperationId);
        }

        _selectedOperationId = selectedOperationId;
        RaisePropertyChanged(nameof(SelectedOperationId));
        RaisePropertyChanged(nameof(SelectedOperation));

        // Batch operation activation, option-set enablement, and persisted-value hydration into one transition so the
        // binding layer does not re-run validation and command preview after every intermediate parameter mutation.
        using (BeginSelectionTransition(SelectionTransitionState.ApplyingOperationState))
        {
            if (!TryApplySelectedOperation(selectedOperation, previousOperation, previousParameters, previousSelectedOperationId))
            {
                return;
            }

            if (!IsSelectionTransitionActive(SelectionTransitionState.HydratingSessionSelection | SelectionTransitionState.ApplyingTargetState))
            {
                RefreshEnabledOptionSets();
                ApplyPersistedSettingsForSelectedTarget();
            }
        }

        RaiseDerivedStateChanged();

        SaveSessionState();
    }

    /// <summary>
    /// Gets whether the operation picker currently has a valid target and at least one compatible operation to show.
    /// </summary>
    public bool IsOperationPickerEnabled => SelectedTarget != null && AvailableOperations.Count > 0;

    /// <summary>
    /// Gets the inline helper text shown by the operation picker before the user chooses an operation.
    /// </summary>
    public string OperationPickerPlaceholderText
    {
        get
        {
            if (SelectedTarget == null)
            {
                return "Select a target first";
            }

            if (AvailableOperations.Count == 0)
            {
                return "No operations available";
            }

            return "Select an operation...";
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
    public bool CanCopyCommand => !string.IsNullOrWhiteSpace(_services.OperationSession.GetPrimaryCommandText(_currentOperation, _parameterSession.RawValue));

    /// <summary>
    /// Gets whether the selected operation currently passes its execution requirements.
    /// </summary>
    public bool CanExecute => _services.OperationSession.CanExecute(_currentOperation, _parameterSession.RawValue);

    /// <summary>
    /// Gets whether the execute warning panel should be shown.
    /// </summary>
    public bool ShowExecuteDisabledReason => !CanExecute;

    /// <summary>
    /// Gets the current execution blocking reason shown in the warning panel.
    /// </summary>
    public string ExecuteDisabledReason => _services.OperationSession.GetExecuteDisabledReason(_currentOperation, _parameterSession.RawValue) ?? string.Empty;

    /// <summary>
    /// Gets the current command preview text.
    /// </summary>
    public string VisibleCommand => _services.OperationSession.GetVisibleCommandText(_currentOperation, _parameterSession.RawValue);

    /// <summary>
    /// Gets the current operation button label.
    /// </summary>
    public string ExecuteButtonText => SelectedOperation?.DisplayName ?? "Execute";

    /// <summary>
    /// Gets the first command preview line for clipboard copy actions.
    /// </summary>
    public string? PrimaryCommandText => _services.OperationSession.GetPrimaryCommandText(_currentOperation, _parameterSession.RawValue);

    /// <summary>
    /// Flushes any queued session and target-setting saves immediately so the most recent UI edits are persisted before
    /// shutdown.
    /// </summary>
    public void FlushPendingSessionState()
    {
        // Capture one final detached copy of the current shell state so shutdown waits for background persistence
        // without touching live UI-owned objects from worker threads.
        _targetOptionValuesSaver.Flush(CaptureTargetOptionValues(SelectedTarget?.Target));
        _sessionSnapshotSaver.Flush(CaptureSessionSnapshot());
    }

    /// <summary>
    /// Detaches the view model from persistence subscriptions and disposes the background savers once the host window
    /// is closing.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _parameterSession.PropertyChanged -= HandleOperationParametersChanged;
        _targetOptionValuesSaver.Dispose();
        _sessionSnapshotSaver.Dispose();
    }

    /// <summary>
    /// Records a transient shell note in the shared log/output stream now that the window no longer reserves space for
    /// a dedicated status banner.
    /// </summary>
    public void SetStatus(string message)
    {
        Status = message;
        ApplicationLogService.LogInformation(message);
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

        ExecutionSession session = _services.ExecutionRuntime.StartExecution(_currentOperation, _parameterSession.RawValue);
        Runtime.AttachExecutionSession(session);
        _services.Execution.AddSession(session);
        SetStatus($"Started {session.OperationName} for {session.TargetName}.");
    }

    /// <summary>
    /// Responds to parameter changes by refreshing only derived shell state that depends on live parameter values.
    /// The enabled option-set list depends on the selected operation and target, not on edits within an existing
    /// option card, so rebuilding the cards here would recreate property-grid editors on every keystroke.
    /// </summary>
    private void HandleOperationParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Ignore nested parameter notifications while session hydration is still rebuilding target, operation, and
        // option state. Refreshing too early can treat restored options as invalid and replace them with defaults.
        if (IsSelectionTransitionActive(SelectionTransitionState.RestoringSession | SelectionTransitionState.ApplyingTargetState | SelectionTransitionState.ApplyingOperationState))
        {
            return;
        }

        QueueOperationParametersRefresh();
    }

    /// <summary>
    /// Coalesces bursts of nested option and parameter notifications into one deferred shell refresh so one property
    /// edit does not synchronously rerun validation, command preview, and persistence capture many times.
    /// </summary>
    private void QueueOperationParametersRefresh()
    {
        if (_operationParametersRefreshQueued)
        {
            return;
        }

        _operationParametersRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _operationParametersRefreshQueued = false;
            if (_disposed)
            {
                return;
            }

            if (IsSelectionTransitionActive(SelectionTransitionState.RestoringSession | SelectionTransitionState.ApplyingTargetState | SelectionTransitionState.ApplyingOperationState))
            {
                return;
            }

            RaiseDerivedStateChanged();
            SaveSessionState();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Captures the current target-scoped option values for one target into a detached batch that can be written later
    /// by the shared background saver.
    /// </summary>
    private PersistedSettingsWriteBatch CaptureTargetOptionValues(IOperationTarget? target)
    {
        if (target == null)
        {
            return new PersistedSettingsWriteBatch();
        }

        TargetSettingsContext context = _services.OptionValues.CreateTargetContext(_services.Targets, target);
        return _services.OptionValues.CaptureOptionValues(_parameterSession.OptionSets, context);
    }

    /// <summary>
    /// Queues a detached copy of the current target-scoped option values so operation or target switches can persist
    /// the outgoing editor state before the live option objects are replaced.
    /// </summary>
    private void QueueTargetOptionValuePersistence(IOperationTarget? target)
    {
        PersistedSettingsWriteBatch batch = CaptureTargetOptionValues(target);
        if (batch.IsEmpty)
        {
            return;
        }

        _targetOptionValuesSaver.RequestSave(batch);
    }

    /// <summary>
    /// Remembers the selected operation for the provided target type so target switches can restore the user's last
    /// compatible operation preference for that target kind.
    /// </summary>
    private void RememberOperationForTargetType(IOperationTarget? target, OperationId? operationId)
    {
        if (target == null || operationId == null)
        {
            // Preserve the last successful remembered operation for this target type when target changes temporarily
            // leave the selection empty during refresh or hydration. Writing null here would erase the preference that
            // the next target switch is trying to restore.
            return;
        }

        TargetTypeId? targetTypeId = _services.Targets.GetTargetTypeId(target);
        if (targetTypeId == null)
        {
            return;
        }

        Dictionary<TargetTypeId, OperationId?> typedSelections = _sessionSnapshot.TypedSelectedOperationIdsByTargetType;
        typedSelections[targetTypeId.Value] = operationId;
        _sessionSnapshot.TypedSelectedOperationIdsByTargetType = typedSelections;
    }

    /// <summary>
    /// Restores the selected target's persisted operation choice and layered settings into the live editing state.
    /// </summary>
    private void RestoreSelectedTargetState()
    {
        using (BeginSelectionTransition(SelectionTransitionState.ApplyingTargetState))
        {
            _parameterSession.ResetOptionSets();

            RefreshEnabledOptionSets();
            ApplyPersistedSettingsForSelectedTarget();
        }
    }

    /// <summary>
    /// Restores the last persisted shell state so the Avalonia shell reopens with the previous targets, selection, and
    /// editable options.
    /// </summary>
    private void RestoreSessionState()
    {
        using (BeginSelectionTransition(SelectionTransitionState.RestoringSession))
        {
            _sessionSnapshot = _sessionPersistence.Load();
            Collection<TargetSessionSnapshot> restoredSnapshots = new();

            foreach (TargetSessionSnapshot targetSnapshot in _sessionSnapshot.Targets)
            {
                if (_sessionPersistence.TryRestoreTarget(targetSnapshot, out IOperationTarget? target) && target != null)
                {
                    Target.AddTargetItem(new TargetListItemViewModel(_services, target));
                    restoredSnapshots.Add(targetSnapshot);
                }
            }

            // Keep only snapshots that successfully restored so nested per-target state survives startup; do not
            // replace them with fresh empty snapshots before the restore pass can apply option values.
            _sessionSnapshot.Targets = restoredSnapshots.ToList();
            Target.NewTargetPath = _sessionSnapshot.PendingTargetPath;

            TargetListItemViewModel? restoredSelection = Target.Targets.FirstOrDefault(item =>
                _sessionPersistence.CreateTargetSnapshot(item.Target).TypedKey == _sessionSnapshot.TypedSelectedTargetKey);

            // Apply the restored target and operation together before recomputing enabled option sets so the hydration
            // pass does not temporarily coerce to a different operation and replace persisted values with defaults.
            using (BeginSelectionTransition(SelectionTransitionState.HydratingSessionSelection))
            {
                Target.SelectedTarget = restoredSelection ?? Target.Targets.FirstOrDefault();
                RefreshOperationSelection();
            }

            RestoreSelectedTargetState();
            RaiseDerivedStateChanged();

            if (Target.SelectedTarget != null)
            {
                Status = $"Restored {Target.Targets.Count} target(s) from the previous Avalonia session.";
            }
        }

        RaiseDerivedStateChanged();
    }

    /// <summary>
    /// Persists the current shell state after user-driven selection or option changes.
    /// </summary>
    private void SaveSessionState()
    {
        if (IsSelectionTransitionActive(SelectionTransitionState.RestoringSession | SelectionTransitionState.ApplyingTargetState))
        {
            return;
        }

        // Persist the current target's layered option values alongside the shell snapshot so all host-managed state
        // uses the same debounced background save behavior.
        _targetOptionValuesSaver.RequestSave(CaptureTargetOptionValues(SelectedTarget?.Target));
        _sessionSnapshotSaver.RequestSave(CaptureSessionSnapshot());
    }

    /// <summary>
    /// Builds a detached session snapshot from the current UI state so the background saver can persist it without
    /// reading mutable view-model collections later.
    /// </summary>
    private SessionSnapshot CaptureSessionSnapshot()
    {
        RememberOperationForTargetType(Target.SelectedTarget?.Target, SelectedOperationId);
        _sessionSnapshot.Targets = Target.Targets.Select(item =>
        {
            TargetSessionSnapshot snapshot = _sessionPersistence.CreateTargetSnapshot(item.Target);
            snapshot.TypedTargetTypeId = _services.Targets.GetTargetTypeId(item.Target) ?? snapshot.TypedTargetTypeId;
            snapshot.Path = _services.Targets.GetTargetPath(item.Target);
            snapshot.TypedKey = _sessionPersistence.BuildTargetKey(snapshot.TypedTargetTypeId, snapshot.Path);
            return snapshot;
        }).ToList();
        _sessionSnapshot.TypedSelectedTargetKey = Target.SelectedTarget?.Target == null ? null : _sessionPersistence.CreateTargetSnapshot(Target.SelectedTarget.Target).TypedKey;
        _sessionSnapshot.PendingTargetPath = Target.NewTargetPath;
        return _sessionPersistence.CloneSnapshot(_sessionSnapshot);
    }

    /// <summary>
    /// Applies shell-level side effects after the target panel changes the selected target.
    /// </summary>
    private void HandleSelectedTargetChanged(TargetListItemViewModel? previousTarget, TargetListItemViewModel? selectedTarget)
    {
        ApplySelectedTargetChange(previousTarget, selectedTarget);
    }

    /// <summary>
    /// Rebuilds the compatible operation list for the currently selected target and preserves the active selection when
    /// it remains valid.
    /// </summary>
    private void RefreshOperationSelection(OperationId? currentSelectionCandidate = null)
    {
        OperationId? previousSelectedOperationId = currentSelectionCandidate ?? _selectedOperationId;
        IReadOnlyList<OperationDescriptor> compatibleOperations = _services.Operations.GetAvailableOperations(SelectedTarget?.Target);

        OperationId? retainedOperationId = _services.OperationSession.ResolveSelectedOperationId(
            compatibleOperations,
            previousSelectedOperationId,
            GetRememberedOperationIdForSelectedTargetType());

        // Publish the rebuilt compatible list and the resolved selected item together so the picker never observes a
        // transient state where the retained operation id points at a list that no longer contains its descriptor.
        AvailableOperations = new ObservableCollection<OperationDescriptor>(compatibleOperations);
        ApplySelectedOperationSelection(retainedOperationId);
    }

    /// <summary>
    /// Applies the newly selected operation by creating the matching runtime operation and parameter object while
    /// preserving the current target and any reusable option state.
    /// </summary>
    private bool TryApplySelectedOperation(
        OperationDescriptor? selectedOperation,
        Operation? previousOperation,
        OperationParameters previousParameters,
        OperationId? previousSelectedOperationId)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ActivateOperation");
        PerformanceTelemetry.SetTag(activity, "operation.id", selectedOperation?.Id.Value ?? string.Empty);
        PerformanceTelemetry.SetTag(activity, "operation.name", selectedOperation?.DisplayName ?? string.Empty);
        PerformanceTelemetry.SetTag(activity, "target.type", SelectedTarget?.Target?.GetType().Name ?? string.Empty);
        PerformanceTelemetry.SetTag(activity, "previous_option_set.count", previousParameters.OptionsInstances.Count);

        try
        {
            _currentOperation = selectedOperation != null ? _services.OperationSession.CreateOperation(selectedOperation.OperationType) : null;
            OperationParameters replacementParameters = _currentOperation?.CreateParameters(previousParameters) ?? new OperationParameters();
            _parameterSession.Replace(replacementParameters, SelectedTarget?.Target as IOperationTarget);
            PerformanceTelemetry.SetTag(activity, "new_option_set.count", replacementParameters.OptionsInstances.Count);
            return true;
        }
        catch (Exception ex)
        {
            PerformanceTelemetry.SetTag(activity, "error.type", ex.GetType().FullName ?? ex.GetType().Name);
            HandleOperationActivationFailure(selectedOperation, previousOperation, previousParameters, previousSelectedOperationId, ex);
            return false;
        }
    }

    /// <summary>
    /// Restores the previous operation state when activation fails so the binding surface never exposes raw exceptions
    /// or leaves the shell with partially-applied runtime state.
    /// </summary>
    private void HandleOperationActivationFailure(
        OperationDescriptor? requestedSelection,
        Operation? previousOperation,
        OperationParameters previousParameters,
        OperationId? previousSelectedOperationId,
        Exception ex)
    {
        _selectedOperationId = previousSelectedOperationId;
        _currentOperation = previousOperation;
        _parameterSession.Replace(previousParameters, SelectedTarget?.Target as IOperationTarget);
        RaisePropertyChanged(nameof(SelectedOperationId));
        RaisePropertyChanged(nameof(SelectedOperation));
        RaiseDerivedStateChanged();
        ApplicationLogService.LogError(ex, "Failed to activate operation '{OperationName}'.", requestedSelection?.DisplayName ?? "<none>");
        SetStatus($"Failed to load '{requestedSelection?.DisplayName ?? "the selected operation"}'. See the application log for details.");
    }

    /// <summary>
    /// Applies the selected target to the live parameter state, refreshes the compatible operation list, restores any
    /// persisted target-specific state, and then queues persistence for the updated selection.
    /// </summary>
    private void ApplySelectedTargetChange(TargetListItemViewModel? previousTarget, TargetListItemViewModel? selectedTarget)
    {
        IOperationTarget? previousOperationTarget = previousTarget?.Target;
        TargetTypeId? previousTargetTypeId = previousOperationTarget == null ? null : _services.Targets.GetTargetTypeId(previousOperationTarget);
        TargetTypeId? selectedTargetTypeId = selectedTarget?.Target == null ? null : _services.Targets.GetTargetTypeId(selectedTarget.Target);
        if (!IsSelectionTransitionActive(SelectionTransitionState.HydratingSessionSelection | SelectionTransitionState.RestoringSession | SelectionTransitionState.ApplyingTargetState) && previousOperationTarget != null)
        {
            QueueTargetOptionValuePersistence(previousOperationTarget);
            RememberOperationForTargetType(previousOperationTarget, _selectedOperationId);
        }

        if (!IsSelectionTransitionActive(SelectionTransitionState.HydratingSessionSelection))
        {
            // Keep target-change side effects suppressed from the moment the live parameter target flips to the new
            // target until the compatible operation list has been recomputed. Otherwise the parameter target change can
            // trigger a session save that records the old operation under the new target type before restore runs.
            using (BeginSelectionTransition(SelectionTransitionState.ApplyingTargetState))
            {
                _parameterSession.Target = selectedTarget?.Target as IOperationTarget;

                // Only carry the current selection forward when the target type stays the same. When the target type
                // changes, prefer that type's remembered operation instead of temporarily applying the old target's
                // operation to the new target.
                OperationId? currentSelectionCandidate = previousTargetTypeId == selectedTargetTypeId ? _selectedOperationId : null;
                RefreshOperationSelection(currentSelectionCandidate);
            }

            RestoreSelectedTargetState();
            RaiseDerivedStateChanged();
        }
        else
        {
            _parameterSession.Target = selectedTarget?.Target as IOperationTarget;
        }

        SaveSessionState();
    }

    /// <summary>
    /// Returns the last remembered operation identifier for the currently selected target type when one exists.
    /// </summary>
    private OperationId? GetRememberedOperationIdForSelectedTargetType()
    {
        TargetTypeId? selectedTargetTypeId = GetSelectedTargetTypeId();
        if (selectedTargetTypeId == null)
        {
            return null;
        }

        if (!_sessionSnapshot.TypedSelectedOperationIdsByTargetType.TryGetValue(selectedTargetTypeId.Value, out OperationId? rememberedOperationId))
        {
            return null;
        }

        return rememberedOperationId;
    }

    /// <summary>
    /// Returns the currently compatible operation descriptor for the provided stable identifier.
    /// </summary>
    private OperationDescriptor? ResolveAvailableOperationById(OperationId? operationId)
    {
        return ResolveOperationById(AvailableOperations, operationId);
    }

    /// <summary>
    /// Returns the operation descriptor with the provided identifier from the supplied compatible operation list.
    /// </summary>
    private static OperationDescriptor? ResolveOperationById(IEnumerable<OperationDescriptor> operations, OperationId? operationId)
    {
        if (operations == null)
        {
            throw new ArgumentNullException(nameof(operations));
        }

        if (operationId == null)
        {
            return null;
        }

        return operations.FirstOrDefault(descriptor => descriptor.Id == operationId.Value);
    }

    /// <summary>
    /// Returns the descriptor id of the currently selected target type so operation memory can be shared across targets
    /// of the same kind.
    /// </summary>
    private TargetTypeId? GetSelectedTargetTypeId()
    {
        return SelectedTarget?.Target == null ? null : _services.Targets.GetTargetTypeId(SelectedTarget.Target);
    }

    /// <summary>
    /// Returns whether any of the provided transition states are currently suppressing selection side effects.
    /// </summary>
    private bool IsSelectionTransitionActive(SelectionTransitionState transitionState)
    {
        return (_selectionTransitionState & transitionState) != SelectionTransitionState.None;
    }

    /// <summary>
    /// Marks one selection transition state as active for the lifetime of the returned scope so nested restore flows
    /// can suppress saves and refreshes without juggling multiple boolean fields.
    /// </summary>
    private IDisposable BeginSelectionTransition(SelectionTransitionState transitionState)
    {
        _selectionTransitionState |= transitionState;
        return new SelectionTransitionScope(this, transitionState);
    }

    /// <summary>
    /// Clears one transition flag when a scoped target or session update finishes.
    /// </summary>
    private void EndSelectionTransition(SelectionTransitionState transitionState)
    {
        _selectionTransitionState &= ~transitionState;
    }

    /// <summary>
    /// Tracks one active transition flag and clears it automatically when the surrounding update scope exits.
    /// </summary>
    private sealed class SelectionTransitionScope : IDisposable
    {
        private readonly MainWindowViewModel _owner;
        private readonly SelectionTransitionState _transitionState;
        private bool _disposed;

        /// <summary>
        /// Creates a scope that keeps one transition flag active until disposal.
        /// </summary>
        public SelectionTransitionScope(MainWindowViewModel owner, SelectionTransitionState transitionState)
        {
            _owner = owner;
            _transitionState = transitionState;
        }

        /// <summary>
        /// Clears the transition flag once the guarded update finishes.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.EndSelectionTransition(_transitionState);
        }
    }

    /// <summary>
    /// Rebuilds the editable option-set list for the currently selected operation and target.
    /// </summary>
    private void RefreshEnabledOptionSets()
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("RefreshEnabledOptionSets");
        var enabledOptionTypes = _services.OperationSession.GetEnabledOptionSetTypes(_currentOperation, SelectedTarget?.Target as IOperationTarget).ToList();
        List<OperationOptions> existingOptionSets = _parameterSession.OptionSets.ToList();
        int addedOptionSetCount = 0;
        int removedOptionSetCount = 0;

        foreach (Type optionSetType in enabledOptionTypes)
        {
            bool alreadyPresent = _parameterSession.OptionSets.Any(options => options.GetType() == optionSetType);
            _parameterSession.EnsureOptionSet(optionSetType);
            if (!alreadyPresent)
            {
                addedOptionSetCount++;
            }
        }

        foreach (OperationOptions options in existingOptionSets)
        {
            if (!enabledOptionTypes.Contains(options.GetType()))
            {
                if (_parameterSession.RemoveOptionSet(options.GetType()))
                {
                    removedOptionSetCount++;
                }
            }
        }

        PerformanceTelemetry.SetTag(activity, "operation.id", _selectedOperationId?.Value ?? string.Empty);
        PerformanceTelemetry.SetTag(activity, "target.type", SelectedTarget?.Target?.GetType().Name ?? string.Empty);
        PerformanceTelemetry.SetTag(activity, "existing_option_set.count", existingOptionSets.Count);
        PerformanceTelemetry.SetTag(activity, "enabled_option_type.count", enabledOptionTypes.Count);
        PerformanceTelemetry.SetTag(activity, "added_option_set.count", addedOptionSetCount);
        PerformanceTelemetry.SetTag(activity, "removed_option_set.count", removedOptionSetCount);
        PerformanceTelemetry.SetTag(activity, "final_option_set.count", _parameterSession.OptionSets.Count);
        RebuildOptionCards();
    }

    /// <summary>
    /// Rebuilds the option-card view models from the current live option instances without recomputing which option
    /// sets are enabled.
    /// </summary>
    private void RebuildOptionCards()
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("RebuildOptionCards");
        PerformanceTelemetry.SetTag(activity, "count", _parameterSession.OptionSets.Count);
        EnabledOptionSets.Clear();

        foreach (OperationOptions options in _parameterSession.OptionSets)
        {
            EnabledOptionSets.Add(new OptionSetViewModel(_services, options, _services.OptionEditors.GetEditorTarget(options)));
        }

        PerformanceTelemetry.SetTag(activity, "option_card.count", EnabledOptionSets.Count);
    }

    /// <summary>
    /// Reapplies the selected target's layered settings onto the live target and option editors after an operation
    /// change or target restore re-creates option instances.
    /// </summary>
    private void ApplyPersistedSettingsForSelectedTarget()
    {
        if (SelectedTarget?.Target == null)
        {
            return;
        }

        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ApplyPersistedSettings");

        TargetSettingsContext context = _services.OptionValues.CreateTargetContext(_services.Targets, SelectedTarget.Target);
        PerformanceTelemetry.SetTag(activity, "target.type", context.TargetTypeId.Value);
        PerformanceTelemetry.SetTag(activity, "count", _parameterSession.OptionSets.Count);
        _services.OptionValues.ApplyOptionValues(_parameterSession.OptionSets, context);

        // Recreate property-grid card targets after applying restored values so adapter-backed editors reflect the
        // rehydrated state instead of the pre-apply values captured when the cards were first created.
        RebuildOptionCards();
    }

    /// <summary>
    /// Raises change notifications for all derived state displayed by the shell.
    /// </summary>
    private void RaiseDerivedStateChanged()
    {
        RaisePropertyChanged(nameof(CanCopyCommand));
        RaisePropertyChanged(nameof(CanExecute));
        RaisePropertyChanged(nameof(ExecuteButtonText));
        RaisePropertyChanged(nameof(ExecuteDisabledReason));
        RaisePropertyChanged(nameof(IsOperationPickerEnabled));
        RaisePropertyChanged(nameof(OperationPickerPlaceholderText));
        RaisePropertyChanged(nameof(PrimaryCommandText));
        RaisePropertyChanged(nameof(ShowExecuteDisabledReason));
        RaisePropertyChanged(nameof(VisibleCommand));
    }

    /// <summary>
    /// Logs background session-save failures into the shared application log.
    /// </summary>
    private static void HandleSessionSaveException(Exception exception)
    {
        ApplicationLogService.LogError(exception, "Failed to save shell session state.");
    }

    /// <summary>
    /// Logs background target-settings save failures into the shared application log.
    /// </summary>
    private static void HandleTargetSettingsSaveException(Exception exception)
    {
        ApplicationLogService.LogError(exception, "Failed to save target-scoped settings.");
    }
}
