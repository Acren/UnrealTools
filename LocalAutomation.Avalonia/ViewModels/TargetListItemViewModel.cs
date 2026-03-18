using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Unreal;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Wraps an operation target with the display-friendly fields needed by the initial Avalonia parity shell.
/// </summary>
public sealed class TargetListItemViewModel : ViewModelBase
{
    /// <summary>
    /// Creates a target list item for the provided operation target.
    /// </summary>
    public TargetListItemViewModel(IOperationTarget target)
    {
        Target = target;
    }

    /// <summary>
    /// Gets the underlying runtime target instance.
    /// </summary>
    public IOperationTarget Target { get; }

    /// <summary>
    /// Gets the display name shown in the target list.
    /// </summary>
    public string DisplayName => Target.DisplayName;

    /// <summary>
    /// Gets the target type label shown in summaries and list rows.
    /// </summary>
    public string TypeName => Target.TypeName;

    /// <summary>
    /// Gets the filesystem path or location backing the target.
    /// </summary>
    public string TargetPath => Target.TargetPath;

    /// <summary>
    /// Gets a concise engine summary when the target exposes engine information.
    /// </summary>
    public string EngineSummary
    {
        get
        {
            if (Target is not IEngineInstanceProvider engineProvider || engineProvider.EngineInstance == null)
            {
                return "Unknown engine";
            }

            return $"{engineProvider.EngineInstance.Version} ({engineProvider.EngineInstance.EngineType})";
        }
    }
}
