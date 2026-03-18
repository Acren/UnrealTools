using System;
using System.Collections.Generic;
using System.Linq;

namespace LocalAutomation.Extensions.Abstractions;

/// <summary>
/// Describes an operation contributed by an extension, including its runtime type and compatible target types.
/// </summary>
public sealed class OperationDescriptor
{
    /// <summary>
    /// Creates an operation descriptor with a stable identifier, display name, runtime type, and compatible target
    /// types.
    /// </summary>
    public OperationDescriptor(string id, string displayName, Type operationType, IEnumerable<Type> supportedTargetTypes, int sortOrder = 0)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        OperationType = operationType ?? throw new ArgumentNullException(nameof(operationType));
        SupportedTargetTypes = (supportedTargetTypes ?? throw new ArgumentNullException(nameof(supportedTargetTypes))).ToArray();
        SortOrder = sortOrder;
    }

    /// <summary>
    /// Gets the stable identifier used to reference the operation.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the display name shown by host UIs.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the runtime operation type instantiated by legacy or bridge layers.
    /// </summary>
    public Type OperationType { get; }

    /// <summary>
    /// Gets the target types this operation can run against.
    /// </summary>
    public IReadOnlyList<Type> SupportedTargetTypes { get; }

    /// <summary>
    /// Gets the host sort order used to preserve a stable, user-friendly operation list.
    /// </summary>
    public int SortOrder { get; }
}
