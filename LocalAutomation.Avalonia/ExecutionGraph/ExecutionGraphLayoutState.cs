using System;
using System.Collections.Generic;
using System.Linq;
using RuntimeExecutionTask = LocalAutomation.Runtime.ExecutionTask;
using RuntimeExecutionTaskId = LocalAutomation.Runtime.ExecutionTaskId;

namespace LocalAutomation.Avalonia.ExecutionGraph;

/// <summary>
/// Owns the persistent measured-width cache that survives across graph refreshes and subsequent layout passes.
/// </summary>
internal sealed class ExecutionGraphLayoutState
{
    private readonly Dictionary<RuntimeExecutionTaskId, double> _measuredNodeWidths = new();

    /// <summary>
    /// Replaces the current measured-width cache with the provided retained widths.
    /// </summary>
    public void ResetMeasuredNodeWidths(IReadOnlyDictionary<RuntimeExecutionTaskId, double> measuredNodeWidths)
    {
        if (measuredNodeWidths == null)
        {
            throw new ArgumentNullException(nameof(measuredNodeWidths));
        }

        _measuredNodeWidths.Clear();
        foreach ((RuntimeExecutionTaskId taskId, double width) in measuredNodeWidths)
        {
            _measuredNodeWidths[taskId] = width;
        }
    }

    /// <summary>
    /// Imports reusable measured widths from another layout state for the visible leaf nodes in the provided snapshot.
    /// </summary>
    public void ImportRetainedLeafWidths(ExecutionGraphLayoutState? sourceState, IReadOnlyList<RuntimeExecutionTask>? tasks, bool revealHiddenTasks)
    {
        if (sourceState == null || tasks == null)
        {
            return;
        }

        foreach ((RuntimeExecutionTaskId taskId, double width) in sourceState.ExportRetainedLeafWidths(tasks, revealHiddenTasks))
        {
            _measuredNodeWidths[taskId] = width;
        }
    }

    /// <summary>
    /// Returns the reusable measured widths for the visible leaf nodes in a future graph snapshot.
    /// </summary>
    public Dictionary<RuntimeExecutionTaskId, double> ExportRetainedLeafWidths(IReadOnlyList<RuntimeExecutionTask> tasks, bool revealHiddenTasks)
    {
        if (tasks == null)
        {
            throw new ArgumentNullException(nameof(tasks));
        }

        /* Width reuse should follow the next visible graph shape rather than the raw authored hierarchy. Building the
           same visible projection here keeps leaf-width retention aligned with hidden-task collapsing rules. */
        ExecutionGraphProjection projection = ExecutionGraphProjection.Create(tasks, revealHiddenTasks);
        return _measuredNodeWidths
            .Where(entry => projection.HasVisibleTask(entry.Key) && !projection.HasChildren(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
    }

    /// <summary>
    /// Stores the measured natural width for one rendered graph control.
    /// </summary>
    public bool TrySetMeasuredNodeWidth(RuntimeExecutionTaskId taskId, double width)
    {
        double measuredWidth = Math.Max(ExecutionGraphLayoutSettings.NodeMinWidth, width);
        if (_measuredNodeWidths.TryGetValue(taskId, out double existingWidth) &&
            Math.Abs(existingWidth - measuredWidth) <= ExecutionGraphLayoutSettings.WidthChangeThreshold)
        {
            return false;
        }

        _measuredNodeWidths[taskId] = measuredWidth;
        return true;
    }

    /// <summary>
    /// Returns the measured width for one node, falling back to the graph minimum width before any control measurement.
    /// </summary>
    public double GetMeasuredNodeWidth(RuntimeExecutionTaskId taskId)
    {
        return _measuredNodeWidths.TryGetValue(taskId, out double width)
            ? width
            : ExecutionGraphLayoutSettings.NodeMinWidth;
    }

    /// <summary>
    /// Returns whether one node still needs a live measurement pass before layout can trust its width.
    /// </summary>
    public bool NeedsMeasurement(RuntimeExecutionTaskId taskId, bool isContainer)
    {
        /* Group containers keep dynamic header content that can widen as metrics and status labels change, so they always
           opt back into measurement instead of trusting an older cached width. */
        return isContainer || !_measuredNodeWidths.ContainsKey(taskId);
    }
}
