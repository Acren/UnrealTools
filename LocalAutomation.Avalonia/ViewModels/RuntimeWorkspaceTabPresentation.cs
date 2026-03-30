namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Describes which panes and workspace affordances a runtime tab should expose.
/// </summary>
public sealed class RuntimeWorkspaceTabPresentation
{
    /// <summary>
    /// Creates one immutable presentation profile for a runtime workspace tab.
    /// </summary>
    public RuntimeWorkspaceTabPresentation(bool showGraph, bool showLog, bool showSubtitle, bool showStatusMarker, bool showRuntimeMetrics)
    {
        ShowGraph = showGraph;
        ShowLog = showLog;
        ShowSubtitle = showSubtitle;
        ShowStatusMarker = showStatusMarker;
        ShowRuntimeMetrics = showRuntimeMetrics;
    }

    /// <summary>
    /// Gets whether the graph pane is visible for this tab.
    /// </summary>
    public bool ShowGraph { get; }

    /// <summary>
    /// Gets whether the log pane is visible for this tab.
    /// </summary>
    public bool ShowLog { get; }

    /// <summary>
    /// Gets whether the tab strip shows the subtitle.
    /// </summary>
    public bool ShowSubtitle { get; }

    /// <summary>
    /// Gets whether the tab strip shows the status marker.
    /// </summary>
    public bool ShowStatusMarker { get; }

    /// <summary>
    /// Gets whether the selected-tab header shows runtime metrics.
    /// </summary>
    public bool ShowRuntimeMetrics { get; }
}
