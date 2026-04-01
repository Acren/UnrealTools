using System;
using System.Diagnostics;

namespace LocalAutomation.Core;

/// <summary>
/// Wraps one optional Activity so instrumentation can remain public without leaking DiagnosticSource runtime types
/// across project boundaries.
/// </summary>
public readonly struct PerformanceActivityScope : IDisposable
{
    private readonly Activity? _activity;

    /// <summary>
    /// Stores the underlying runtime Activity when tracing is enabled.
    /// </summary>
    internal PerformanceActivityScope(Activity? activity)
    {
        _activity = activity;
    }

    /// <summary>
    /// Adds one tag to the underlying activity when tracing is enabled.
    /// </summary>
    public PerformanceActivityScope SetTag(string key, object? value)
    {
        _activity?.SetTag(key, value);
        return this;
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
/// Owns the shared ActivitySource used to trace performance-sensitive workflows across the shell, application, and
/// Unreal-specific layers.
/// </summary>
public static class PerformanceTelemetry
{
    /// <summary>
    /// Gets the source name used by listeners to subscribe to performance telemetry activities.
    /// </summary>
    public const string SourceName = "LocalAutomation.PerformanceTelemetry";

    /// <summary>
    /// Gets the shared ActivitySource used for performance telemetry tracing.
    /// </summary>
    public static ActivitySource Source { get; } = new(SourceName);

    /// <summary>
    /// Starts one traced activity when a listener is attached, otherwise returns <c>null</c> with near-zero overhead.
    /// </summary>
    public static PerformanceActivityScope StartActivity(string activityName)
    {
        return new PerformanceActivityScope(Source.StartActivity(activityName, ActivityKind.Internal));
    }

}
