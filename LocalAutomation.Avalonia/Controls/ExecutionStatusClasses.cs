using Avalonia.Controls;
using RuntimeExecutionTaskStatus = LocalAutomation.Runtime.ExecutionTaskStatus;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Centralizes the semantic execution-graph classes used by controls and generated edge paths.
/// </summary>
internal static class ExecutionStatusClasses
{
    /// <summary>
     /// Applies the shared interaction classes used by execution-graph styles.
     /// </summary>
    public static void ApplyInteractionClasses(Classes classes, bool isSelected, bool isHovered, bool isPressed)
    {
        classes.Set("selected", isSelected);
        classes.Set("hover", isHovered);
        classes.Set("pressed", isPressed);
    }

    /// <summary>
    /// Applies the shared semantic status classes used by execution-graph styles.
    /// </summary>
    public static void ApplyStatusClasses(Classes classes, RuntimeExecutionTaskStatus status)
    {
        /* Graph styling keys follow the user-facing semantic outcome. Callers pass lifecycle state separately when they
           need active-running affordances such as animation. */
        classes.Set("pending", status is RuntimeExecutionTaskStatus.Pending);
        classes.Set("planned", status is RuntimeExecutionTaskStatus.Planned);
        classes.Set("skipped", status is RuntimeExecutionTaskStatus.Skipped);
        classes.Set("disabled", status is RuntimeExecutionTaskStatus.Disabled);
        classes.Set("running", status is RuntimeExecutionTaskStatus.Running);
        classes.Set("completed", status is RuntimeExecutionTaskStatus.Completed);
        classes.Set("failed", status is RuntimeExecutionTaskStatus.Failed);
        classes.Set("cancelled", status is RuntimeExecutionTaskStatus.Cancelled);
        classes.Set("interrupted", status is RuntimeExecutionTaskStatus.Interrupted);
    }
}
