using System;
using LocalAutomation.Extensions.Abstractions;
using LocalAutomation.Runtime;
using UnrealAutomationCommon.Unreal;

namespace LocalAutomation.Extensions.Unreal;

/// <summary>
/// Creates Unreal target instances from filesystem paths by reusing the existing target-detection helpers.
/// </summary>
public sealed class UnrealPathTargetFactory : ITargetFactory
{
    /// <summary>
    /// Gets the stable identifier for the Unreal path-based target factory.
    /// </summary>
    public string Id => "unreal.path-target-factory";

    /// <summary>
    /// Attempts to create a supported Unreal target instance from the provided path.
    /// </summary>
    public bool TryCreateTarget(string source, out IOperationTarget? target)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            target = null;
            return false;
        }

        if (ProjectPaths.Instance.IsTargetDirectory(source))
        {
            target = new Project(source);
            return true;
        }

        if (PluginPaths.Instance.IsTargetDirectory(source))
        {
            target = new Plugin(source);
            return true;
        }

        if (PackagePaths.Instance.IsTargetDirectory(source))
        {
            target = new Package(source);
            return true;
        }

        if (EnginePaths.Instance.IsTargetDirectory(source))
        {
            target = new Engine(source);
            return true;
        }

        target = null;
        return false;
    }
}
