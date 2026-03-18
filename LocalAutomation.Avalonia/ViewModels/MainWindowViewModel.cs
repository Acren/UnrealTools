using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using LocalAutomation.Extensions.Abstractions;
using LocalAutomationApplicationHost = LocalAutomation.Application.LocalAutomationApplicationHost;
using UnrealAutomationCommon.Operations;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Drives the first real Avalonia shell by exposing target selection, operation selection, and command preview state
/// through the shared LocalAutomation services.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly LocalAutomationApplicationHost _services;
    private readonly OperationParameters _operationParameters = new();
    private object? _currentOperation;
    private string _newTargetPath = string.Empty;
    private OperationDescriptor? _selectedOperation;
    private TargetListItemViewModel? _selectedTarget;
    private string _status = "Add a target path to begin using the LocalAutomation shell.";

    /// <summary>
    /// Creates the Avalonia main window view model around the shared LocalAutomation application host.
    /// </summary>
    public MainWindowViewModel(LocalAutomationApplicationHost services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _operationParameters.PropertyChanged += HandleOperationParametersChanged;
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
    /// Gets the required option set names for the current target and operation selection.
    /// </summary>
    public ObservableCollection<string> EnabledOptionSetNames { get; } = new();

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
                RefreshOperationSelection();
                RaiseDerivedStateChanged();
                RaisePropertyChanged(nameof(CanRemoveTarget));
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

                RefreshEnabledOptionSetNames();
                RaiseDerivedStateChanged();
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
    /// Gets whether the selected target can currently be removed.
    /// </summary>
    public bool CanRemoveTarget => SelectedTarget != null;

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
                return true;
            }

            TargetListItemViewModel targetItem = new(target);
            Targets.Add(targetItem);
            SelectedTarget = targetItem;
            NewTargetPath = string.Empty;
            Status = $"Added {target.TypeName.ToLowerInvariant()} target '{target.DisplayName}'.";
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
            return;
        }

        int nextIndex = Math.Clamp(removedIndex, 0, Targets.Count - 1);
        SelectedTarget = Targets[nextIndex];
        Status = $"Removed target '{removedTargetName}'.";
    }

    /// <summary>
    /// Updates the current status line when the shell needs to surface a transient user-facing note.
    /// </summary>
    public void SetStatus(string message)
    {
        Status = message;
    }

    /// <summary>
    /// Responds to parameter changes by refreshing the derived UI state that depends on operation parameters.
    /// </summary>
    private void HandleOperationParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshEnabledOptionSetNames();
        RaiseDerivedStateChanged();
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
    /// Rebuilds the human-readable option set list for the currently selected operation and target.
    /// </summary>
    private void RefreshEnabledOptionSetNames()
    {
        EnabledOptionSetNames.Clear();
        foreach (Type optionSetType in _services.OperationSession.GetEnabledOptionSetTypes(_currentOperation, SelectedTarget?.Target))
        {
            string displayName = optionSetType.Name.Replace("Options", string.Empty);
            EnabledOptionSetNames.Add(displayName);
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
        RaisePropertyChanged(nameof(SelectedTargetEngine));
        RaisePropertyChanged(nameof(SelectedTargetPath));
        RaisePropertyChanged(nameof(SelectedTargetSummary));
        RaisePropertyChanged(nameof(VisibleCommand));
    }
}
