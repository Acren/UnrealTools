using Microsoft.Extensions.Logging;

namespace LocalAutomation.Core;

/// <summary>
/// Represents a single log line surfaced by the shared LocalAutomation execution services.
/// </summary>
public sealed class LogEntry
{
    /// <summary>
    /// Gets or sets the formatted log message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the log severity used for styling and summaries.
    /// </summary>
    public LogLevel Verbosity { get; set; }
}
