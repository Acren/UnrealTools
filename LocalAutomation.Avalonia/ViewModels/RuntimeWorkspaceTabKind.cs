namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Distinguishes the permanent workspace tabs from execution-session tabs.
/// </summary>
public enum RuntimeWorkspaceTabKind
{
    /// <summary>
    /// Represents the pinned application log tab.
    /// </summary>
    ApplicationLog,

    /// <summary>
    /// Represents the pinned live plan-preview tab.
    /// </summary>
    PlanPreview,

    /// <summary>
    /// Represents one started execution session.
    /// </summary>
    ExecutionSession
}
