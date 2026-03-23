using System;
using System.Diagnostics;

namespace LocalAutomation.Application.Diagnostics;

/// <summary>
/// Owns the shared ActivitySource used to trace operation-switch performance across the shell and application layers.
/// </summary>
public static class OperationSwitchTelemetry
{
    /// <summary>
    /// Gets the source name used by listeners to subscribe to operation-switch activities.
    /// </summary>
    public const string SourceName = "LocalAutomation.OperationSwitch";

    /// <summary>
    /// Gets the shared ActivitySource used for operation-switch tracing.
    /// </summary>
    public static ActivitySource Source { get; } = new(SourceName);

    /// <summary>
    /// Starts one traced activity when a listener is attached, otherwise returns <c>null</c> with near-zero overhead.
    /// </summary>
    public static Activity? StartActivity(string activityName)
    {
        return Source.StartActivity(activityName, ActivityKind.Internal);
    }

    /// <summary>
    /// Adds one tag value when the activity exists, avoiding repetitive null checks at instrumentation sites.
    /// </summary>
    public static void SetTag(Activity? activity, string key, object? value)
    {
        activity?.SetTag(key, value);
    }
}
