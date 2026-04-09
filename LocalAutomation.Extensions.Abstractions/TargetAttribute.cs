using System;

namespace LocalAutomation.Extensions.Abstractions;

/// <summary>
/// Marks one runtime target type as user-discoverable for extension auto-registration.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class TargetAttribute : Attribute
{
    /// <summary>
    /// Gets or sets an optional display name override. When omitted, the runtime type name becomes the descriptor name.
    /// </summary>
    public string? DisplayName { get; init; }
}
