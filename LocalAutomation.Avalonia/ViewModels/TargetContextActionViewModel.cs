using System;
using LocalAutomation.Extensions.Abstractions;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Adapts an extension-provided target context action into a button-friendly Avalonia view model.
/// </summary>
public sealed class TargetContextActionViewModel : ViewModelBase
{
    private readonly Action _execute;

    /// <summary>
    /// Creates a target action view model around the provided descriptor and runtime callback.
    /// </summary>
    public TargetContextActionViewModel(ContextActionDescriptor descriptor, Action execute)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    /// <summary>
    /// Gets the underlying descriptor for diagnostics and future expansion.
    /// </summary>
    public ContextActionDescriptor Descriptor { get; }

    /// <summary>
    /// Gets the label rendered on the action button.
    /// </summary>
    public string DisplayName => Descriptor.DisplayName;

    /// <summary>
    /// Executes the bound action callback.
    /// </summary>
    public void Execute()
    {
        _execute();
    }
}
