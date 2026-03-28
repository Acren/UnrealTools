using System.Collections;
using System.Linq;
using LocalAutomation.Runtime;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OptionChoiceSources;

/// <summary>
/// Supplies the installed launcher engine versions used by Unreal option editors.
/// </summary>
public sealed class InstalledEngineVersionChoiceSource : IChoiceCollectionSource
{
    /// <summary>
    /// Returns the installed launcher engine versions that can be selected for an operation.
    /// </summary>
    public IEnumerable GetChoices(object? component, string propertyName)
    {
        return EngineFinder.GetLauncherEngineInstallVersions().ToList();
    }
}
