using System;
using System.Collections.Generic;

namespace LocalAutomation.Core;

/// <summary>
/// Exposes a buffered log stream that UI hosts can observe while operations are running.
/// </summary>
public interface ILogStream
{
    /// <summary>
    /// Raised when a new log entry is appended.
    /// </summary>
    event Action<LogEntry>? EntryAdded;

    /// <summary>
    /// Gets the current buffered entries.
    /// </summary>
    IReadOnlyList<LogEntry> Entries { get; }

    /// <summary>
    /// Clears the buffered log entries.
    /// </summary>
    void Clear();
}
