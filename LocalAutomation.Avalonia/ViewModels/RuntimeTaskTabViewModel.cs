using System;
using System.Collections.ObjectModel;
using LocalAutomation.Core;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Represents one runtime tab in the Avalonia shell, either the permanent application log tab or an execution task
/// tab backed by a shared execution session.
/// </summary>
public sealed class RuntimeTaskTabViewModel : ViewModelBase
{
    private bool _isSelected;

    /// <summary>
    /// Creates a runtime tab view model with the provided display metadata and optional execution session.
    /// </summary>
    public RuntimeTaskTabViewModel(string id, string title, string subtitle, bool isApplicationLog, ExecutionSession? session = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Subtitle = subtitle ?? throw new ArgumentNullException(nameof(subtitle));
        IsApplicationLog = isApplicationLog;
        Session = session;
    }

    /// <summary>
    /// Gets the stable tab identifier.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the short title shown in the runtime tab strip.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the descriptive subtitle shown above the selected runtime log stream.
    /// </summary>
    public string Subtitle { get; }

    /// <summary>
    /// Gets whether this tab is the permanent application log tab.
    /// </summary>
    public bool IsApplicationLog { get; }

    /// <summary>
    /// Gets the execution session backing this runtime tab when it represents a task.
    /// </summary>
    public ExecutionSession? Session { get; }

    /// <summary>
    /// Gets the log entries displayed in the runtime panel for this tab.
    /// </summary>
    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = new();

    /// <summary>
    /// Gets or sets whether this tab is currently selected.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Gets whether the tab represents a running task.
    /// </summary>
    public bool IsRunning => Session is { IsRunning: true };

    /// <summary>
    /// Gets whether the selected task can be terminated from the runtime panel.
    /// </summary>
    public bool CanTerminate => Session is { IsRunning: true };

    /// <summary>
    /// Gets whether the tab can be closed by the user.
    /// </summary>
    public bool CanClose => !IsApplicationLog;

    /// <summary>
    /// Adds a log entry to the tab and raises runtime-derived change notifications.
    /// </summary>
    public void AddLogEntry(LogEntryViewModel entry)
    {
        LogEntries.Add(entry);
        RaisePropertyChanged(nameof(IsRunning));
        RaisePropertyChanged(nameof(CanTerminate));
    }

    /// <summary>
    /// Raises change notifications after the backing execution session changes state.
    /// </summary>
    public void NotifyStateChanged()
    {
        RaisePropertyChanged(nameof(IsRunning));
        RaisePropertyChanged(nameof(CanTerminate));
    }
}
