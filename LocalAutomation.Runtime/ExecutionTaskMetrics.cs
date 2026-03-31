using System;

namespace LocalAutomation.Runtime;

/// <summary>
/// Represents the runtime metrics shown for one execution task subtree or for an entire execution session.
/// </summary>
public readonly struct ExecutionTaskMetrics
{
    public ExecutionTaskMetrics(TimeSpan? duration, int warningCount, int errorCount)
    {
        Duration = duration;
        WarningCount = warningCount;
        ErrorCount = errorCount;
    }

    public static ExecutionTaskMetrics Empty { get; } = new(duration: null, warningCount: 0, errorCount: 0);

    public TimeSpan? Duration { get; }

    public int WarningCount { get; }

    public int ErrorCount { get; }

    public bool HasWarnings => WarningCount > 0;

    public bool HasErrors => ErrorCount > 0;
}
