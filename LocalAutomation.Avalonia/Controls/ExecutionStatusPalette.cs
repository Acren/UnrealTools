using LocalAutomation.Core;
using Avalonia.Controls;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Centralizes the execution-graph status palette so controls and edge rendering resolve the same semantic hues.
/// </summary>
internal static class ExecutionStatusPalette
{
    /// <summary>
    /// Resolves one semantic execution status to the shared graph accent color.
    /// </summary>
    public static string GetColor(ExecutionTaskStatus status)
    {
        return status switch
        {
            ExecutionTaskStatus.Completed => "#7BE37B",
            ExecutionTaskStatus.Running => "#63A8FF",
            ExecutionTaskStatus.Failed => "#FF5C5C",
            ExecutionTaskStatus.Blocked => "#F1D479",
            /* Pending and Planned intentionally share one neutral preview/queued treatment, while Cancelled, Disabled,
               and Skipped share a more muted inactive treatment. */
            ExecutionTaskStatus.Pending => "#98A9B8",
            ExecutionTaskStatus.Planned => "#98A9B8",
            ExecutionTaskStatus.Cancelled => "#7A838D",
            ExecutionTaskStatus.Disabled => "#7A838D",
            ExecutionTaskStatus.Skipped => "#7A838D",
            _ => "#89919A"
        };
    }

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
