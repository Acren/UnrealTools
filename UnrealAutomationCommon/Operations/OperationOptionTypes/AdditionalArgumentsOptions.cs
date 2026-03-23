using LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes;

/// <summary>
/// Stores freeform pass-through arguments as a normal option set so newer UIs can render it through the same option
/// pipeline as the rest of the operation configuration.
/// </summary>
public sealed class AdditionalArgumentsOptions : OperationOptions
{
    /// <summary>
    /// Keep this option set at the end of the list because it acts as a final override layer rather than a primary
    /// configuration category.
    /// </summary>
    public override int SortIndex => 10_000;

    /// <summary>
    /// Give the option set a clearer title than the default type-name splitting.
    /// </summary>
    public override string Name => "Additional Arguments";

    /// <summary>
    /// Gets the raw argument string appended to the generated command.
    /// </summary>
    public Option<string> Arguments { get; } = string.Empty;
}
