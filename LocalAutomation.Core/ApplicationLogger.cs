using System;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Core;

/// <summary>
/// Stores the process-wide logger used by bridge-era hosts and adapters so shared runtime code can write through the
/// active application logger without depending on a specific UI project.
/// </summary>
public static class ApplicationLogger
{
    private static ILogger? _logger;

    /// <summary>
    /// Gets or sets the active process-wide logger.
    /// </summary>
    public static ILogger Logger
    {
        get => _logger ?? throw new InvalidOperationException("Application logger has not been initialized.");
        set => _logger = value ?? throw new ArgumentNullException(nameof(value));
    }
}
