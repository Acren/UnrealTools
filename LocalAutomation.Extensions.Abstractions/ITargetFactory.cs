using LocalAutomation.Runtime;

namespace LocalAutomation.Extensions.Abstractions;

/// <summary>
/// Creates extension-defined runtime targets from host-provided source data such as filesystem paths.
/// </summary>
public interface ITargetFactory
{
    /// <summary>
    /// Gets the stable identifier for the target factory.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Attempts to create a target instance from the provided source value.
    /// </summary>
    bool TryCreateTarget(string source, out IOperationTarget? target);
}
