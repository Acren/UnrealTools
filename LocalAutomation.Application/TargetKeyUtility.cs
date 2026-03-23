using System;
using LocalAutomation.Extensions.Abstractions;

namespace LocalAutomation.Application;

/// <summary>
/// Builds the stable key used anywhere the app needs to persist or correlate target-scoped state.
/// </summary>
public static class TargetKeyUtility
{
    /// <summary>
    /// Combines the target descriptor identifier and target path into one stable persistence key.
    /// </summary>
    public static TargetKey BuildTargetKey(TargetTypeId targetTypeId, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("Target path must be provided.", nameof(targetPath));
        }

        return new TargetKey($"{targetTypeId.Value}|{targetPath}");
    }

    /// <summary>
    /// Combines the serialized target descriptor identifier and target path into one stable persistence key.
    /// </summary>
    public static string BuildTargetKey(string targetTypeId, string targetPath)
    {
        TargetTypeId typedTargetTypeId = new(targetTypeId);
        return BuildTargetKey(typedTargetTypeId, targetPath).Value;
    }
}
