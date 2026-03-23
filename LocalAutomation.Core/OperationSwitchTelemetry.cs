using System;
using System.Diagnostics;

namespace LocalAutomation.Core;

/// <summary>
/// Wraps one optional Activity so instrumentation can remain public without leaking DiagnosticSource runtime types
/// across project boundaries.
/// </summary>
public readonly struct OperationSwitchActivityScope : IDisposable
{
    private readonly Activity? _activity;

    /// <summary>
    /// Stores the underlying runtime Activity when tracing is enabled.
    /// </summary>
    internal OperationSwitchActivityScope(Activity? activity)
    {
        _activity = activity;
    }

    /// <summary>
    /// Adds one tag to the underlying activity when tracing is enabled.
    /// </summary>
    public void SetTag(string key, object? value)
    {
        _activity?.SetTag(key, value);
    }

    /// <summary>
    /// Disposes the underlying activity when tracing is enabled.
    /// </summary>
    public void Dispose()
    {
        _activity?.Dispose();
    }
}

/// <summary>
/// Owns the shared ActivitySource used to trace operation-switch performance across the shell, application, and
/// Unreal-specific layers.
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
    public static OperationSwitchActivityScope StartActivity(string activityName)
    {
        return new OperationSwitchActivityScope(Source.StartActivity(activityName, ActivityKind.Internal));
    }

    /// <summary>
    /// Adds one tag value when the activity exists, avoiding repetitive null checks at instrumentation sites.
    /// </summary>
    public static void SetTag(OperationSwitchActivityScope activity, string key, object? value)
    {
        activity.SetTag(key, value);
    }
}
