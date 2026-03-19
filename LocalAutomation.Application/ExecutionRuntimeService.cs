using System;
using LocalAutomation.Core;
using LocalAutomation.Extensions.Abstractions;

namespace LocalAutomation.Application;

/// <summary>
/// Resolves runner adapters and starts shared execution sessions for runtime operations.
/// </summary>
public sealed class ExecutionRuntimeService
{
    private readonly ExtensionCatalog _catalog;

    /// <summary>
    /// Creates an execution runtime service around the shared extension catalog.
    /// </summary>
    public ExecutionRuntimeService(ExtensionCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>
    /// Starts a shared execution session for the provided runtime operation.
    /// </summary>
    public LocalAutomation.Core.ExecutionSession StartExecution(object operation, object parameters)
    {
        if (operation == null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        return GetAdapter(operation).StartExecution(operation, parameters);
    }

    /// <summary>
    /// Finds the registered runner adapter responsible for the provided runtime operation.
    /// </summary>
    private IRunnerAdapter GetAdapter(object operation)
    {
        foreach (IRunnerAdapter adapter in _catalog.RunnerAdapters)
        {
            if (adapter.CanHandle(operation))
            {
                return adapter;
            }
        }

        throw new InvalidOperationException($"No registered runner adapter can handle '{operation.GetType().FullName}'.");
    }
}
