using System;

#nullable enable

namespace LocalAutomation.Runtime;

/// <summary>
/// Extends the shared runtime parameter state with Unreal-specific engine resolution hooks.
/// </summary>
public abstract class UnrealRuntimeOperationParameters<TEnvironment> : OperationParameters where TEnvironment : class
{
    /// <summary>
    /// Gets or sets an Unreal-specific environment override used by child operations.
    /// </summary>
    public TEnvironment? EnvironmentOverride { get; set; }

    /// <summary>
    /// Returns the effective Unreal environment for the current target and option state.
    /// </summary>
    public abstract TEnvironment? GetEnvironment();

    /// <summary>
    /// Copies Unreal-specific environment overrides onto child parameter sets.
    /// </summary>
    public override OperationParameters CreateChild()
    {
        UnrealRuntimeOperationParameters<TEnvironment> child = (UnrealRuntimeOperationParameters<TEnvironment>)base.CreateChild();
        child.EnvironmentOverride = EnvironmentOverride;
        return child;
    }
}
