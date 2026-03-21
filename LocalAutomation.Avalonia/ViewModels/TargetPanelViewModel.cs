using System;
using System.Collections.ObjectModel;
using System.Linq;
using LocalAutomation.Extensions.Abstractions;
using Microsoft.Extensions.Logging;
using LocalAutomationApplicationHost = LocalAutomation.Application.LocalAutomationApplicationHost;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Owns target-picker state, target-scoped actions, and target management interactions for the target panel.
/// </summary>
public sealed class TargetPanelViewModel : ViewModelBase
{
    private readonly LocalAutomationApplicationHost _services;
    private readonly Action<TargetListItemViewModel?, TargetListItemViewModel?> _handleSelectedTargetChanged;
    private readonly Action _handleTargetsChanged;
    private readonly Action<string> _setStatus;
    private readonly TargetPickerItemViewModel _addTargetPickerItem = TargetPickerItemViewModel.CreateAddAction();
    private string _newTargetPath = string.Empty;
    private TargetPickerItemViewModel? _selectedTargetPickerItem;
    private TargetListItemViewModel? _selectedTarget;

    /// <summary>
    /// Creates the target panel view model around the shared target services and shell callbacks.
    /// </summary>
    public TargetPanelViewModel(
        LocalAutomationApplicationHost services,
        Action<string> setStatus,
        Action<TargetListItemViewModel?, TargetListItemViewModel?> handleSelectedTargetChanged,
        Action handleTargetsChanged)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _handleSelectedTargetChanged = handleSelectedTargetChanged ?? throw new ArgumentNullException(nameof(handleSelectedTargetChanged));
        _handleTargetsChanged = handleTargetsChanged ?? throw new ArgumentNullException(nameof(handleTargetsChanged));
        TargetPickerItems.Add(_addTargetPickerItem);
    }

    /// <summary>
    /// Gets the real targets shown by the shell.
    /// </summary>
    public ObservableCollection<TargetListItemViewModel> Targets { get; } = new();

    /// <summary>
    /// Gets the target-picker rows, including the synthetic add-target action shown at the bottom of the dropdown.
    /// </summary>
    public ObservableCollection<TargetPickerItemViewModel> TargetPickerItems { get; } = new();

    /// <summary>
    /// Gets the target actions currently available for the selected target.
    /// </summary>
    public ObservableCollection<TargetContextActionViewModel> TargetActions { get; } = new();

    /// <summary>
    /// Gets or sets the path text entered into the add-target flow.
    /// </summary>
    public string NewTargetPath
    {
        get => _newTargetPath;
        set
        {
            SetProperty(ref _newTargetPath, value);
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
            TargetListItemViewModel? previousTarget = _selectedTarget;
            if (!SetProperty(ref _selectedTarget, value))
            {
                return;
            }

            SyncSelectedTargetPickerItem();
            RefreshTargetActions();
            _handleSelectedTargetChanged(previousTarget, value);
        }
    }

    /// <summary>
    /// Creates a target from the current path input and adds it to the in-memory session.
    /// </summary>
    public bool TryAddTargetFromInput(out string? errorMessage)
    {
        errorMessage = null;
        string source = NewTargetPath.Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            errorMessage = "Enter a target path first.";
            return false;
        }

        try
        {
            object createdTarget = _services.Targets.CreateTarget(source);
            if (!_services.Targets.IsTarget(createdTarget))
            {
                errorMessage = $"Created target '{createdTarget.GetType().Name}' is not recognized by the registered target catalog.";
                return false;
            }

            TargetListItemViewModel? existingTarget = Targets.FirstOrDefault(item =>
                string.Equals(item.TargetPath, _services.Targets.GetTargetPath(createdTarget), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.TypeName, _services.Targets.GetTypeName(createdTarget), StringComparison.Ordinal));

            if (existingTarget != null)
            {
                SelectedTarget = existingTarget;
                _setStatus($"Selected existing {existingTarget.TypeName.ToLowerInvariant()} target '{existingTarget.DisplayName}'.");
                _handleTargetsChanged();
                return true;
            }

            TargetListItemViewModel targetItem = new(_services, createdTarget);
            AddTargetItem(targetItem);
            SelectedTarget = targetItem;
            NewTargetPath = string.Empty;
            _setStatus($"Added {_services.Targets.GetTypeName(createdTarget).ToLowerInvariant()} target '{_services.Targets.GetDisplayName(createdTarget)}'.");
            _handleTargetsChanged();
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
            _setStatus($"Removed target '{removedTargetName}'. Add another target path to continue.");
            _handleTargetsChanged();
            return;
        }

        int nextIndex = Math.Clamp(removedIndex, 0, Targets.Count - 1);
        SelectedTarget = Targets[nextIndex];
        _setStatus($"Removed target '{removedTargetName}'.");
        _handleTargetsChanged();
    }

    /// <summary>
    /// Removes a specific target row, preserving the previous selection when the removed item was not selected.
    /// </summary>
    public void RemoveTarget(TargetListItemViewModel targetItem)
    {
        if (targetItem == null)
        {
            throw new ArgumentNullException(nameof(targetItem));
        }

        if (ReferenceEquals(SelectedTarget, targetItem))
        {
            RemoveSelectedTarget();
            return;
        }

        TargetListItemViewModel? previousTarget = SelectedTarget;
        SelectedTarget = targetItem;
        RemoveSelectedTarget();

        if (previousTarget != null && !ReferenceEquals(SelectedTarget, previousTarget))
        {
            SelectedTarget = previousTarget;
        }
    }

    /// <summary>
    /// Executes one of the currently available target actions.
    /// </summary>
    public void ExecuteTargetAction(TargetContextActionViewModel? action)
    {
        action?.Execute();
    }

    /// <summary>
    /// Reports a target-panel-specific status message through the shared shell logger.
    /// </summary>
    public void SetStatus(string message)
    {
        _setStatus(message);
    }

    /// <summary>
    /// Adds a restored or newly created real target row to both the working target list and the picker list.
    /// </summary>
    public void AddTargetItem(TargetListItemViewModel targetItem)
    {
        Targets.Add(targetItem);
        TargetPickerItems.Insert(Math.Max(0, TargetPickerItems.Count - 1), new TargetPickerItemViewModel(targetItem));
    }

    /// <summary>
    /// Rebuilds the target actions available for the selected target from the shared extension catalog.
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
            _setStatus($"Executed '{descriptor.DisplayName}' for '{SelectedTarget?.DisplayName}'.");
        }
        catch (Exception ex)
        {
            ApplicationLogService.LogError(ex, "Target action '{ActionName}' failed.", descriptor.DisplayName);
            _setStatus($"Failed to run '{descriptor.DisplayName}': {ex.Message}");
        }
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
}
