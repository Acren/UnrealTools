using System;
using LocalAutomation.Runtime;
using LocalAutomationApplicationHost = LocalAutomation.Application.LocalAutomationApplicationHost;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Adapts an operation option set into a property-grid-backed card for the Avalonia parity shell.
/// </summary>
public class OptionSetViewModel : ViewModelBase
{
    private readonly LocalAutomationApplicationHost _services;

    /// <summary>
      /// Creates an option set view model around a runtime operation options instance.
      /// </summary>
    public OptionSetViewModel(LocalAutomationApplicationHost services, OperationOptions options, object? propertyGridTarget = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
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
    public string Name => _services.OperationRuntime.GetOptionSetName(Options);

}
