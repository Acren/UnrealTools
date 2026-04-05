using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Avalonia.Diagnostics;

/// <summary>
/// Listens to instrumented performance activities and writes a compact, human-readable timing summary into the
/// existing shell logs when performance telemetry is explicitly enabled for the current host.
/// </summary>
public static class PerformanceTelemetryListener
{
    private static readonly object Sync = new();
    private static readonly ConcurrentDictionary<string, ActivitySummary> Summaries = new();
    private static ActivityListener? _listener;
    private static bool _enabled;
    private static bool _isStarted;
    private static TimeSpan _minimumDuration = TimeSpan.Zero;

    /// <summary>
    /// Starts the shared listener once when performance telemetry is enabled.
    /// </summary>
    public static void Start(bool enabled, TimeSpan? minimumDuration = null)
    {
        lock (Sync)
        {
            _enabled = enabled;
            _minimumDuration = minimumDuration ?? TimeSpan.Zero;

            if (_isStarted)
            {
                return;
            }

            if (!enabled)
            {
                return;
            }

            _listener = new ActivityListener
            {
                ShouldListenTo = source => string.Equals(source.Name, PerformanceTelemetry.SourceName, StringComparison.Ordinal),
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = HandleActivityStopped
            };

            ActivitySource.AddActivityListener(_listener);
            _isStarted = true;
        }
    }

    /// <summary>
    /// Captures completed activities and writes the final formatted tree whenever a root telemetry activity ends.
    /// </summary>
    private static void HandleActivityStopped(Activity activity)
    {
        if (!_enabled)
        {
            return;
        }

        string traceId = activity.TraceId.ToString();
        ActivitySummary summary = Summaries.GetOrAdd(traceId, _ => new ActivitySummary());
        summary.Add(activity);

        // Any top-level activity is a complete telemetry tree, so avoid baking in knowledge of a specific workflow name.
        if (activity.Parent != null)
        {
            return;
        }

        if (Summaries.TryRemove(traceId, out ActivitySummary? completedSummary))
        {
            if (activity.Duration < _minimumDuration)
            {
                return;
            }

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
            RecordedActivity recordedRoot = summary.Activities.Single(activity => activity.SpanId == rootActivity.SpanId);
            Dictionary<ActivitySpanId, List<RecordedActivity>> childrenByParent = summary.Activities
                .OrderBy(activity => activity.StartTimeUtc)
                .ThenBy(activity => activity.OperationName, StringComparer.Ordinal)
                .GroupBy(activity => activity.ParentSpanId)
                .ToDictionary(group => group.Key, group => group.ToList());

            List<string> lines = new();
            lines.Add(BuildRootLine(recordedRoot));

            foreach (RecordedActivity child in GetChildren(childrenByParent, rootActivity.SpanId))
            {
                AppendActivity(lines, childrenByParent, child, recordedRoot.StartTimeUtc, depth: 1);
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
    private static void AppendActivity(List<string> lines, IReadOnlyDictionary<ActivitySpanId, List<RecordedActivity>> childrenByParent, RecordedActivity activity, DateTime rootStartTimeUtc, int depth)
    {
        string indent = new(' ', depth * 2);
        IReadOnlyList<RecordedActivity> children = GetChildren(childrenByParent, activity.SpanId);
        TimeSpan startOffset = activity.StartTimeUtc - rootStartTimeUtc;
        if (startOffset < TimeSpan.Zero)
        {
            startOffset = TimeSpan.Zero;
        }

        TimeSpan exclusiveDuration = CalculateExclusiveDuration(activity, children);
        string tags = FormatTags(activity);
        string line = $"{indent}+{FormatMilliseconds(startOffset)} ms total={FormatMilliseconds(activity.Duration)} ms self={FormatMilliseconds(exclusiveDuration)} ms {activity.OperationName}";
        if (!string.IsNullOrEmpty(tags))
        {
            line += " " + tags;
        }

        lines.Add(line);

        foreach (RecordedActivity child in children)
        {
            AppendActivity(lines, childrenByParent, child, rootStartTimeUtc, depth + 1);
        }
    }

    /// <summary>
    /// Builds the one-line root summary that identifies the traced workflow before the nested timing tree.
    /// </summary>
    private static string BuildRootLine(RecordedActivity rootActivity)
    {
        List<string> parts =
        [
            "PerformanceTelemetry",
            rootActivity.OperationName
        ];

        string tags = FormatTags(rootActivity);
        if (!string.IsNullOrEmpty(tags))
        {
            parts.Add(tags);
        }

        parts.Add($"{FormatMilliseconds(rootActivity.Duration)} ms");
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Returns the already-ordered child activities for one parent span id.
    /// </summary>
    private static IReadOnlyList<RecordedActivity> GetChildren(IReadOnlyDictionary<ActivitySpanId, List<RecordedActivity>> childrenByParent, ActivitySpanId parentSpanId)
    {
        return childrenByParent.TryGetValue(parentSpanId, out List<RecordedActivity>? children)
            ? children
            : Array.Empty<RecordedActivity>();
    }

    /// <summary>
    /// Computes true exclusive time for one activity by subtracting the merged coverage of its direct child spans.
    /// </summary>
    private static TimeSpan CalculateExclusiveDuration(RecordedActivity activity, IReadOnlyList<RecordedActivity> children)
    {
        if (children.Count == 0)
        {
            return activity.Duration;
        }

        long activityStartTicks = activity.StartTimeUtc.Ticks;
        long activityEndTicks = activityStartTicks + activity.Duration.Ticks;
        List<(long StartTicks, long EndTicks)> childIntervals = new(children.Count);
        foreach (RecordedActivity child in children)
        {
            long childStartTicks = Math.Max(activityStartTicks, child.StartTimeUtc.Ticks);
            long childEndTicks = Math.Min(activityEndTicks, child.StartTimeUtc.Ticks + child.Duration.Ticks);
            if (childEndTicks > childStartTicks)
            {
                childIntervals.Add((childStartTicks, childEndTicks));
            }
        }

        if (childIntervals.Count == 0)
        {
            return activity.Duration;
        }

        childIntervals.Sort((left, right) => left.StartTicks.CompareTo(right.StartTicks));
        long coveredTicks = 0;
        long currentStartTicks = childIntervals[0].StartTicks;
        long currentEndTicks = childIntervals[0].EndTicks;
        for (int i = 1; i < childIntervals.Count; i++)
        {
            (long nextStartTicks, long nextEndTicks) = childIntervals[i];
            if (nextStartTicks <= currentEndTicks)
            {
                currentEndTicks = Math.Max(currentEndTicks, nextEndTicks);
                continue;
            }

            coveredTicks += currentEndTicks - currentStartTicks;
            currentStartTicks = nextStartTicks;
            currentEndTicks = nextEndTicks;
        }

        coveredTicks += currentEndTicks - currentStartTicks;
        long exclusiveTicks = Math.Max(0, activity.Duration.Ticks - coveredTicks);
        return TimeSpan.FromTicks(exclusiveTicks);
    }

    /// <summary>
    /// Formats the stored tag snapshot into a stable, readable key-value sequence.
    /// </summary>
    private static string FormatTags(RecordedActivity activity, params string[] excludedKeys)
    {
        HashSet<string> excluded = excludedKeys.Length == 0
            ? []
            : new HashSet<string>(excludedKeys, StringComparer.Ordinal);

        List<string> parts = new();
        foreach (KeyValuePair<string, object?> tag in activity.Tags)
        {
            if (excluded.Contains(tag.Key))
            {
                continue;
            }

            string formattedValue = FormatTagValue(tag.Key, tag.Value);
            if (string.IsNullOrWhiteSpace(formattedValue))
            {
                continue;
            }

            parts.Add($"{tag.Key}={formattedValue}");
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Formats one tag value for display, shortening path-like values and quoting values that contain whitespace.
    /// </summary>
    private static string FormatTagValue(string key, object? value)
    {
        string formattedValue = FormatRawTagValue(value);
        if (formattedValue.Length == 0)
        {
            return formattedValue;
        }

        if (key.EndsWith(".path", StringComparison.Ordinal))
        {
            formattedValue = ShortenPath(formattedValue);
        }

        return RequiresQuotes(formattedValue)
            ? $"\"{formattedValue.Replace("\"", "'")}\""
            : formattedValue;
    }

    /// <summary>
    /// Converts one raw tag value into a trimmed invariant string.
    /// </summary>
    private static string FormatRawTagValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string text => text.Replace(Environment.NewLine, " ").Trim(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture)?.Replace(Environment.NewLine, " ").Trim() ?? string.Empty,
            _ => value.ToString()?.Replace(Environment.NewLine, " ").Trim() ?? string.Empty
        };
    }

    /// <summary>
    /// Shortens a filesystem path to the last two segments so logs stay readable while preserving useful identity.
    /// </summary>
    private static string ShortenPath(string path)
    {
        string[] segments = path
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();
        if (segments.Length <= 2)
        {
            return path;
        }

        return $"...\\{string.Join('\\', segments.Skip(segments.Length - 2))}";
    }

    /// <summary>
    /// Returns whether a formatted value should be quoted to keep multi-word content readable in the summary line.
    /// </summary>
    private static bool RequiresQuotes(string value)
    {
        return value.Any(ch => char.IsWhiteSpace(ch) || ch == '=' || ch == '"');
    }

    /// <summary>
    /// Formats one duration as rounded milliseconds for compact tree output.
    /// </summary>
    private static string FormatMilliseconds(TimeSpan duration)
    {
        return duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Stores the activities captured for one traced telemetry root.
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
            Tags = activity.TagObjects.ToArray();
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
        /// Gets the raw tag snapshot captured from the stopped activity.
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, object?>> Tags { get; }

    }
}
