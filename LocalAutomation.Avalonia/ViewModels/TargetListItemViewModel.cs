using System;
using LocalAutomation.Runtime;
using LocalAutomationApplicationHost = LocalAutomation.Application.LocalAutomationApplicationHost;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Wraps an operation target with the display-friendly fields needed by the initial Avalonia parity shell.
/// </summary>
public sealed class TargetListItemViewModel : ViewModelBase
{
    private readonly LocalAutomationApplicationHost _services;

    /// <summary>
     /// Creates a target list item for the provided operation target.
     /// </summary>
    public TargetListItemViewModel(LocalAutomationApplicationHost services, IOperationTarget target)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        if (!_services.Targets.IsTarget(target))
        {
            // Keep host-facing validation errors aligned with the active launcher branding so branded shells do not leak
            // the generic LocalAutomation product name in user-visible diagnostics.
            throw new ArgumentException($"Target is not recognized by the registered {App.ShellIdentity.ApplicationName} target catalog.", nameof(target));
        }

        Target = target;
    }

    /// <summary>
     /// Gets the underlying runtime target instance.
     /// </summary>
    public IOperationTarget Target { get; }

    /// <summary>
     /// Gets the display name shown in the target list.
     /// </summary>
    public string DisplayName => _services.Targets.GetDisplayName(Target);

    /// <summary>
     /// Gets the target type label shown in summaries and list rows.
     /// </summary>
    public string TypeName => _services.Targets.GetTypeName(Target);

    /// <summary>
     /// Gets the filesystem path or location backing the target.
     /// </summary>
    public string TargetPath => _services.Targets.GetTargetPath(Target);

}
