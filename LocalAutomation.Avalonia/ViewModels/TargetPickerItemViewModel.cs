namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Wraps either a real target row or the synthetic add-target action so the picker can present both in one dropdown.
/// </summary>
public sealed class TargetPickerItemViewModel : ViewModelBase
{
    /// <summary>
    /// Creates a picker item for a concrete target already tracked by the shell.
    /// </summary>
    public TargetPickerItemViewModel(TargetListItemViewModel targetItem)
        : this(targetItem, false)
    {
    }

    /// <summary>
    /// Creates the synthetic picker entry that opens the folder browser when selected.
    /// </summary>
    public static TargetPickerItemViewModel CreateAddAction()
    {
        return new TargetPickerItemViewModel(null, true);
    }

    /// <summary>
    /// Initializes a picker item that may represent a real target or the add-target affordance.
    /// </summary>
    private TargetPickerItemViewModel(TargetListItemViewModel? targetItem, bool isAddAction)
    {
        TargetItem = targetItem;
        IsAddAction = isAddAction;
    }

    /// <summary>
    /// Gets the backing target row when this picker entry represents a real target.
    /// </summary>
    public TargetListItemViewModel? TargetItem { get; }

    /// <summary>
    /// Gets whether this picker row is the synthetic add-target action instead of a real target.
    /// </summary>
    public bool IsAddAction { get; }

    /// <summary>
    /// Gets whether this picker row represents a real target and should use the richer target layout.
    /// </summary>
    public bool IsTargetItem => !IsAddAction;

    /// <summary>
    /// Gets the primary line shown in the dropdown and collapsed picker.
    /// </summary>
    public string DisplayName => TargetItem?.DisplayName ?? "+ Add target";

    /// <summary>
    /// Gets the short label used by the dedicated add-action row.
    /// </summary>
    public string AddActionLabel => "+ Add target";

    /// <summary>
    /// Gets the badge label shown alongside the picker entry.
    /// </summary>
    public string TypeName => TargetItem?.TypeName ?? "Browse";

    /// <summary>
    /// Gets the descriptive path line shown under the primary label.
    /// </summary>
    public string TargetPath => TargetItem?.TargetPath ?? "Open the folder picker to add a project, plugin, package, or engine target.";

    /// <summary>
    /// Gets the tertiary summary line shown at the bottom of the picker entry.
    /// </summary>
    public string DetailText => TargetItem?.TypeName ?? "Folder picker";
}
