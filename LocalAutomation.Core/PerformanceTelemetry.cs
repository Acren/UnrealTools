using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

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
    /// Disambiguates the caller-member overload from the explicit string overload while still allowing a zero-argument
    /// call at instrumentation sites.
    /// </summary>
    public readonly struct CallerActivityName
    {
    }

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

    /// <summary>
    /// Starts one traced activity named after the calling member when the method name alone is descriptive enough for
    /// the telemetry trace.
    /// </summary>
    public static PerformanceActivityScope StartActivity(CallerActivityName _ = default, [CallerMemberName] string memberName = "")
    {
        return StartActivity(string.IsNullOrWhiteSpace(memberName) ? "UnknownActivity" : memberName);
    }

    /// <summary>
    /// Starts one traced activity named after the supplied caller type and the calling member when a readable
    /// <c>Class.Method</c> span name is sufficient.
    /// </summary>
    public static PerformanceActivityScope StartActivity<TCaller>([CallerMemberName] string memberName = "")
    {
        return StartActivity(BuildActivityName(typeof(TCaller), memberName));
    }

    /// <summary>
    /// Formats one caller type and member into a readable <c>Class.Method</c> activity name.
    /// </summary>
    private static string BuildActivityName(Type callerType, string memberName)
    {
        string typeName = callerType?.Name ?? string.Empty;
        int genericSuffixIndex = typeName.IndexOf('`');
        if (genericSuffixIndex >= 0)
        {
            /* Strip generic arity markers so telemetry names stay readable even when the caller type is generic. */
            typeName = typeName.Substring(0, genericSuffixIndex);
        }

        string formattedMemberName = string.IsNullOrWhiteSpace(memberName) ? "UnknownActivity" : memberName;
        return string.IsNullOrWhiteSpace(typeName)
            ? formattedMemberName
            : typeName + "." + formattedMemberName;
    }

}
