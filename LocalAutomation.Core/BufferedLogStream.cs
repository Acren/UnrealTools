using System;
using System.Collections.Generic;

namespace LocalAutomation.Core;

/// <summary>
/// Stores log entries in memory and notifies subscribers as new output arrives.
/// </summary>
public sealed class BufferedLogStream : ILogStream
{
    private readonly List<LogEntry> _entries = new();
    private readonly object _syncRoot = new();

    /// <summary>
    /// Raised whenever a new log entry is appended.
    /// </summary>
    public event Action<LogEntry>? EntryAdded;

    /// <summary>
    /// Gets the current buffered entries.
    /// </summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_syncRoot)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>
    /// Appends a new log entry and notifies subscribers.
    /// </summary>
    public void Add(LogEntry entry)
    {
        lock (_syncRoot)
        {
            _entries.Add(entry);
        }

        EntryAdded?.Invoke(entry);
    }

    /// <summary>
    /// Clears the buffered log entries.
    /// </summary>
    public void Clear()
    {
        lock (_syncRoot)
        {
            _entries.Clear();
        }
    }
}
