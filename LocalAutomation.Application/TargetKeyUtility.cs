using System;

namespace LocalAutomation.Application;

/// <summary>
/// Builds the stable key used anywhere the app needs to persist or correlate target-scoped state.
/// </summary>
public static class TargetKeyUtility
{
    /// <summary>
    /// Combines the target descriptor identifier and target path into one stable persistence key.
    /// </summary>
    public static string BuildTargetKey(string targetTypeId, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetTypeId))
        {
            throw new ArgumentException("Target type id must be provided.", nameof(targetTypeId));
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("Target path must be provided.", nameof(targetPath));
        }

        return $"{targetTypeId}|{targetPath}";
    }
}
