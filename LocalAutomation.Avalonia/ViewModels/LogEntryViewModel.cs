using System;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Represents a single log line rendered by the Avalonia execution output panel.
/// </summary>
public sealed class LogEntryViewModel
{
    /// <summary>
    /// Creates a UI log entry from the formatted message and severity.
    /// </summary>
    public LogEntryViewModel(string message, LogLevel verbosity, DateTimeOffset? timestamp = null)
    {
        Message = message;
        Verbosity = verbosity;
        Timestamp = timestamp ?? DateTimeOffset.Now;
    }

    /// <summary>
    /// Gets the formatted message text.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets the local timestamp captured when the log line was added to the UI.
    /// </summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// Gets the formatted timestamp prefix shown before the log text.
    /// </summary>
    public string TimestampText => Timestamp.ToString("HH:mm:ss");

    /// <summary>
    /// Gets the severity for styling.
    /// </summary>
    public LogLevel Verbosity { get; }

    /// <summary>
    /// Gets whether the log line should be styled as an error.
    /// </summary>
    public bool IsError => Verbosity >= LogLevel.Error;

    /// <summary>
    /// Gets whether the log line should be styled as a warning.
    /// </summary>
    public bool IsWarning => Verbosity == LogLevel.Warning;

    /// <summary>
    /// Gets the foreground color used by the output list.
    /// </summary>
    public string Foreground
    {
        get
        {
            if (IsError)
            {
                return "#E65050";
            }

            if (IsWarning)
            {
                return "#E6E60A";
            }

            return "#E6E6E6";
        }
    }
}
