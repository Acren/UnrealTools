using System;
using LocalAutomation.Runtime;

namespace LocalAutomation.Application;

/// <summary>
/// Carries the runtime target identity needed to resolve layered persistence file locations.
/// </summary>
public sealed class TargetSettingsContext
{
    /// <summary>
    /// Creates a persistence context for one target.
    /// </summary>
    public TargetSettingsContext(IOperationTarget target, string targetTypeId, string targetPath, string targetKey)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        TargetTypeId = targetTypeId ?? throw new ArgumentNullException(nameof(targetTypeId));
        TargetPath = targetPath ?? throw new ArgumentNullException(nameof(targetPath));
        TargetKey = targetKey ?? throw new ArgumentNullException(nameof(targetKey));
    }

    /// <summary>
    /// Gets the live runtime target.
    /// </summary>
    public IOperationTarget Target { get; }

    /// <summary>
    /// Gets the registered target descriptor id.
    /// </summary>
    public string TargetTypeId { get; }

    /// <summary>
    /// Gets the stable path used to recreate the target.
    /// </summary>
    public string TargetPath { get; }

    /// <summary>
    /// Gets the appdata key used for per-target overrides.
    /// </summary>
    public string TargetKey { get; }
}
