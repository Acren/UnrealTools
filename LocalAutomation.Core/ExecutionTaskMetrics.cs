using System;

namespace LocalAutomation.Core;

/// <summary>
/// Represents the shared runtime metrics shown for one execution task subtree or for an entire execution session.
/// </summary>
public readonly struct ExecutionTaskMetrics
{
    /// <summary>
    /// Creates one immutable execution-metrics value.
    /// </summary>
    public ExecutionTaskMetrics(TimeSpan? duration, int warningCount, int errorCount)
    {
        Duration = duration;
        WarningCount = warningCount;
        ErrorCount = errorCount;
    }

    /// <summary>
    /// Gets the empty metrics value used when no execution data exists yet.
    /// </summary>
    public static ExecutionTaskMetrics Empty { get; } = new(duration: null, warningCount: 0, errorCount: 0);

    /// <summary>
    /// Gets the elapsed duration represented by the metrics when runtime timing is available.
    /// </summary>
    public TimeSpan? Duration { get; }

    /// <summary>
    /// Gets the total warning count represented by the metrics.
    /// </summary>
    public int WarningCount { get; }

    /// <summary>
    /// Gets the total error count represented by the metrics.
    /// </summary>
    public int ErrorCount { get; }

    /// <summary>
    /// Gets whether the metrics contain any warning lines.
    /// </summary>
    public bool HasWarnings => WarningCount > 0;

    /// <summary>
    /// Gets whether the metrics contain any error lines.
    /// </summary>
    public bool HasErrors => ErrorCount > 0;
}
