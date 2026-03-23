using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LocalAutomation.Application.Diagnostics;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Avalonia.Diagnostics;

/// <summary>
/// Listens to operation-switch activities and writes a compact, human-readable timing summary into the existing shell
/// logs when performance diagnostics are explicitly enabled for the current host.
/// </summary>
public static class OperationSwitchDiagnosticsListener
{
    private static readonly object Sync = new();
    private static readonly ConcurrentDictionary<string, ActivitySummary> Summaries = new();
    private static ActivityListener? _listener;
    private static bool _isStarted;

    /// <summary>
    /// Starts the shared listener once when diagnostics are enabled.
    /// </summary>
    public static void Start(bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        lock (Sync)
        {
            if (_isStarted)
            {
                return;
            }

            _listener = new ActivityListener
            {
                ShouldListenTo = source => string.Equals(source.Name, OperationSwitchTelemetry.SourceName, StringComparison.Ordinal),
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = HandleActivityStopped
            };

            ActivitySource.AddActivityListener(_listener);
            _isStarted = true;
        }
    }

    /// <summary>
    /// Captures completed activities and writes the final formatted tree when the root operation-switch activity ends.
    /// </summary>
    private static void HandleActivityStopped(Activity activity)
    {
        string traceId = activity.TraceId.ToString();
        ActivitySummary summary = Summaries.GetOrAdd(traceId, _ => new ActivitySummary());
        summary.Add(activity);

        if (activity.Parent != null || !string.Equals(activity.OperationName, "OperationSwitch", StringComparison.Ordinal))
        {
            return;
        }

        if (Summaries.TryRemove(traceId, out ActivitySummary? completedSummary))
        {
            LogSummary(activity, completedSummary);
        }
    }

    /// <summary>
    /// Writes one readable timing tree to the shared application logger.
    /// </summary>
    private static void LogSummary(Activity rootActivity, ActivitySummary summary)
    {
        try
        {
            string rootOperationName = rootActivity.GetTagItem("operation.name")?.ToString()
                ?? rootActivity.GetTagItem("operation.id")?.ToString()
                ?? "<unknown>";

            List<string> lines = new();
            lines.Add($"OperationSwitch {rootOperationName} {rootActivity.Duration.TotalMilliseconds:0} ms");

            foreach (RecordedActivity child in summary.Activities.OrderBy(item => item.StartTimeUtc).Where(item => item.ParentSpanId == rootActivity.SpanId))
            {
                AppendActivity(lines, summary, child, depth: 1);
            }

            ApplicationLogger.Logger.LogInformation(string.Join(Environment.NewLine, lines));
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Appends one activity and its nested children using an indented, readable tree format.
    /// </summary>
    private static void AppendActivity(List<string> lines, ActivitySummary summary, RecordedActivity activity, int depth)
    {
        string indent = new(' ', depth * 2);
        string description = activity.Description;
        lines.Add($"{indent}{activity.OperationName} {description} {activity.Duration.TotalMilliseconds:0} ms".TrimEnd());

        foreach (RecordedActivity child in summary.Activities.OrderBy(item => item.StartTimeUtc).Where(item => item.ParentSpanId == activity.SpanId))
        {
            AppendActivity(lines, summary, child, depth + 1);
        }
    }

    /// <summary>
    /// Stores the activities captured for one traced operation switch.
    /// </summary>
    private sealed class ActivitySummary
    {
        /// <summary>
        /// Gets the captured activity list for one trace.
        /// </summary>
        public List<RecordedActivity> Activities { get; } = new();

        /// <summary>
        /// Adds one completed activity snapshot to the summary.
        /// </summary>
        public void Add(Activity activity)
        {
            Activities.Add(new RecordedActivity(activity));
        }
    }

    /// <summary>
    /// Stores the small immutable subset of activity data needed to build the final timing tree after the runtime
    /// Activity instance has been stopped and returned to the listener.
    /// </summary>
    private sealed class RecordedActivity
    {
        /// <summary>
        /// Captures the completed activity's identifying and timing information.
        /// </summary>
        public RecordedActivity(Activity activity)
        {
            OperationName = activity.OperationName;
            StartTimeUtc = activity.StartTimeUtc;
            Duration = activity.Duration;
            SpanId = activity.SpanId;
            ParentSpanId = activity.ParentSpanId;
            Description = BuildDescription(activity);
        }

        /// <summary>
        /// Gets the activity operation name.
        /// </summary>
        public string OperationName { get; }

        /// <summary>
        /// Gets when the activity started.
        /// </summary>
        public DateTime StartTimeUtc { get; }

        /// <summary>
        /// Gets the completed activity duration.
        /// </summary>
        public TimeSpan Duration { get; }

        /// <summary>
        /// Gets the span id used to connect this node to its children.
        /// </summary>
        public ActivitySpanId SpanId { get; }

        /// <summary>
        /// Gets the parent span id used to locate the parent node.
        /// </summary>
        public ActivitySpanId ParentSpanId { get; }

        /// <summary>
        /// Gets the concise, formatted tag summary shown next to this activity in the shell log output.
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Builds one compact description from the tags most useful for debugging the operation-switch path.
        /// </summary>
        private static string BuildDescription(Activity activity)
        {
            List<string> parts = new();

            string? operationName = activity.GetTagItem("operation.name")?.ToString();
            string? operationId = activity.GetTagItem("operation.id")?.ToString();
            string? targetType = activity.GetTagItem("target.type")?.ToString();
            string? optionSetType = activity.GetTagItem("option_set.type")?.ToString();
            string? count = activity.GetTagItem("count")?.ToString();
            string? cacheHit = activity.GetTagItem("cache.hit")?.ToString();

            if (!string.IsNullOrWhiteSpace(operationName))
            {
                parts.Add(operationName);
            }
            else if (!string.IsNullOrWhiteSpace(operationId))
            {
                parts.Add(operationId);
            }

            if (!string.IsNullOrWhiteSpace(targetType))
            {
                parts.Add($"target={targetType}");
            }

            if (!string.IsNullOrWhiteSpace(optionSetType))
            {
                parts.Add($"option={optionSetType}");
            }

            if (!string.IsNullOrWhiteSpace(count))
            {
                parts.Add($"count={count}");
            }

            if (!string.IsNullOrWhiteSpace(cacheHit))
            {
                parts.Add($"cache={cacheHit}");
            }

            return parts.Count == 0 ? string.Empty : string.Join(" ", parts);
        }
    }
}
