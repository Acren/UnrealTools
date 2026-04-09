using System;

namespace LocalAutomation.Core;

/// <summary>
/// Centralizes the human-readable duration formatting used by operational logs.
/// </summary>
public static class DurationFormatting
{
    /// <summary>
    /// Formats one duration in the same seconds-based shape used by finished-section logs.
    /// </summary>
    public static string FormatSeconds(TimeSpan duration)
    {
        /* Log durations should never print as negative values even if a caller provides an unexpected time span. */
        TimeSpan resolvedDuration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        return $"{resolvedDuration.TotalSeconds:0.00} s";
    }
}
