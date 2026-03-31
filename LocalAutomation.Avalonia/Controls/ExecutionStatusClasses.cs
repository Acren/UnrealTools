using Avalonia.Controls;
using LocalAutomation.Core;

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
    public static void ApplyStatusClasses(Classes classes, ExecutionTaskStatus status)
    {
        classes.Set("pending", status is ExecutionTaskStatus.Pending);
        classes.Set("planned", status is ExecutionTaskStatus.Planned);
        classes.Set("skipped", status is ExecutionTaskStatus.Skipped);
        classes.Set("disabled", status is ExecutionTaskStatus.Disabled);
        classes.Set("running", status is ExecutionTaskStatus.Running);
        classes.Set("completed", status is ExecutionTaskStatus.Completed);
        classes.Set("failed", status is ExecutionTaskStatus.Failed);
        classes.Set("blocked", status is ExecutionTaskStatus.Blocked);
        classes.Set("cancelled", status is ExecutionTaskStatus.Cancelled);
    }
}
