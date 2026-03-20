using System;
using UnrealAutomationCommon.Operations;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Adapts an operation option set into a property-grid-backed card for the Avalonia parity shell.
/// </summary>
public class OptionSetViewModel : ViewModelBase
{
    /// <summary>
     /// Creates an option set view model around a runtime operation options instance.
     /// </summary>
    public OptionSetViewModel(OperationOptions options, object? propertyGridTarget = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        PropertyGridTarget = propertyGridTarget ?? options;
    }

    /// <summary>
    /// Gets the underlying runtime option set.
    /// </summary>
    public OperationOptions Options { get; }

    /// <summary>
    /// Gets the object currently presented to the property grid for this option set.
    /// </summary>
    public object PropertyGridTarget { get; }

    /// <summary>
    /// Gets the title shown by the shell.
    /// </summary>
    public string Name => Options.Name;

}
