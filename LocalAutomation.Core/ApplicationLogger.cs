using System;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Core;

/// <summary>
/// Stores the process-wide logger used by shared runtime and shell code.
/// </summary>
public static class ApplicationLogger
{
    private static ILogger? _logger;

    public static ILogger Logger
    {
        get => _logger ?? throw new InvalidOperationException("Application logger has not been initialized.");
        set => _logger = value ?? throw new ArgumentNullException(nameof(value));
    }
}
