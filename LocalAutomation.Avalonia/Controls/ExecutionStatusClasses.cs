using Avalonia.Controls;
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
    public static void ApplyStatusClasses(Classes classes, ExecutionTaskDisplayStatus status)
    {
        /* Graph styling keys follow the user-facing semantic outcome. Callers pass lifecycle state separately when they
           need active-running affordances such as animation. */
        classes.Set("pending", status is ExecutionTaskDisplayStatus.Queued or ExecutionTaskDisplayStatus.WaitingForDependencies);
        classes.Set("queued", status is ExecutionTaskDisplayStatus.Queued);
        classes.Set("waiting-for-dependencies", status is ExecutionTaskDisplayStatus.WaitingForDependencies);
        classes.Set("awaiting-lock", status is ExecutionTaskDisplayStatus.AwaitingLock);
        classes.Set("planned", status is ExecutionTaskDisplayStatus.Planned);
        classes.Set("skipped", status is ExecutionTaskDisplayStatus.Skipped);
        classes.Set("disabled", status is ExecutionTaskDisplayStatus.Disabled);
        classes.Set("running", status is ExecutionTaskDisplayStatus.Running);
        classes.Set("completed", status is ExecutionTaskDisplayStatus.Completed);
        classes.Set("failed", status is ExecutionTaskDisplayStatus.Failed);
        classes.Set("cancelled", status is ExecutionTaskDisplayStatus.Cancelled);
        classes.Set("interrupted", status is ExecutionTaskDisplayStatus.Interrupted);
    }
}
