using System;
using System.Collections.Generic;
using LocalAutomation.Extensions.Abstractions;

namespace LocalAutomation.Avalonia.Bootstrap;

/// <summary>
/// Captures the outcome of bundled extension discovery so startup can continue with partial success while still
/// surfacing actionable diagnostics.
/// </summary>
public sealed class ExtensionLoadResult
{
    /// <summary>
    /// Gets the successfully created extension modules in discovery order.
    /// </summary>
    public List<IExtensionModule> Modules { get; } = new();

    /// <summary>
    /// Gets the human-readable startup warnings collected while scanning bundled extensions.
    /// </summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Gets the human-readable startup errors collected while scanning bundled extensions.
    /// </summary>
    public List<string> Errors { get; } = new();

    /// <summary>
    /// Gets a concise startup summary suitable for a status banner or empty-state warning.
    /// </summary>
    public string CreateStartupMessage()
    {
        if (Errors.Count == 0 && Warnings.Count == 0)
        {
            return string.Empty;
        }

        return $"Loaded {Modules.Count} extension(s); {Warnings.Count} warning(s), {Errors.Count} error(s). See logs for details.";
    }
}
