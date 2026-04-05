using System;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Core;

/// <summary>
/// Represents a single log line surfaced by the shared LocalAutomation execution services.
/// </summary>
public sealed class LogEntry
{
    /// <summary>
    /// Gets or sets the local timestamp captured when the log line was created.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// Gets or sets the execution-session identifier associated with the log line when one exists.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Gets or sets the execution-task identifier associated with the log line when one exists.
    /// </summary>
    public string? TaskId { get; set; }

    /// <summary>
    /// Gets or sets the formatted log message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the log severity used for styling and summaries.
    /// </summary>
    public LogLevel Verbosity { get; set; }
}
