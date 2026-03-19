using System;
using System.Threading.Tasks;
using LocalAutomation.Core;

namespace LocalAutomation.Extensions.Abstractions;

/// <summary>
/// Bridges extension-specific execution engines into the generic application layer.
/// </summary>
public interface IRunnerAdapter
{
    /// <summary>
    /// Gets the stable identifier for this runner adapter.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Returns whether this adapter can execute the provided runtime operation instance.
    /// </summary>
    bool CanHandle(object operation);

    /// <summary>
    /// Starts an execution session for the provided operation and parameter state.
    /// </summary>
    ExecutionSession StartExecution(object operation, object parameters);
}
