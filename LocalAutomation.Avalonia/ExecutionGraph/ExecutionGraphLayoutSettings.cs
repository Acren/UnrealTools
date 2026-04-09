namespace LocalAutomation.Avalonia.ExecutionGraph;

/// <summary>
/// Centralizes the execution-graph layout constants so the projection, layout engine, view models, and controls all use
/// the same geometry rules.
/// </summary>
public static class ExecutionGraphLayoutSettings
{
    /// <summary>
    /// Defines the minimum width used for graph nodes so short titles still read as graph nodes rather than badges.
    /// </summary>
    public const double NodeMinWidth = 156;

    /// <summary>
    /// Defines the fixed height used for leaf task cards.
    /// </summary>
    public const double NodeHeight = 100;

    /// <summary>
    /// Defines the fixed height reserved for a group-container header.
    /// </summary>
    public const double GroupHeaderHeight = 56;

    /// <summary>
    /// Defines the padding between a group-container border and its child items.
    /// </summary>
    public const double GroupPadding = 18;

    /// <summary>
    /// Defines the spacing between dependency-stage columns.
    /// </summary>
    public const double ColumnGap = 84;

    /// <summary>
    /// Defines the spacing between vertically stacked sibling nodes.
    /// </summary>
    public const double RowGap = 30;

    /// <summary>
    /// Defines the outer graph margin used for the initial layout origin and trailing canvas padding.
    /// </summary>
    public const double CanvasMargin = 24;

    /// <summary>
    /// Defines the minimum change that warrants updating a cached measured width.
    /// </summary>
    public const double WidthChangeThreshold = 0.5;

    /// <summary>
    /// Keeps dependency lines away from rounded node corners so straight edges read as intentional face-to-face routes.
    /// </summary>
    public const double EdgeCornerBuffer = 16.0;

    /// <summary>
    /// Preserves enough horizontal travel before elbow edges turn vertical.
    /// </summary>
    public const double EdgeElbowHorizontalInset = 28.0;
}
