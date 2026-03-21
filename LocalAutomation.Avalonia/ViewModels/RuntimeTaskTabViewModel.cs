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
    private int _errorCount;
    private int _warningCount;

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
    /// Gets whether the runtime tab has a non-empty subtitle worth rendering.
    /// </summary>
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);

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
    /// Gets the number of warning log entries collected for this runtime tab.
    /// </summary>
    public int WarningCount => _warningCount;

    /// <summary>
    /// Gets the number of error log entries collected for this runtime tab.
    /// </summary>
    public int ErrorCount => _errorCount;

    /// <summary>
    /// Gets whether the warning count should be accent-colored in the selected task header.
    /// </summary>
    public bool HasWarnings => WarningCount > 0;

    /// <summary>
    /// Gets whether the error count should be accent-colored in the selected task header.
    /// </summary>
    public bool HasErrors => ErrorCount > 0;

    /// <summary>
    /// Gets the elapsed runtime shown beside the terminate action for the selected task.
    /// </summary>
    public string DurationText
    {
        get
        {
            if (Session == null)
            {
                return "--:--";
            }

            DateTimeOffset endTime = Session.FinishedAt ?? DateTimeOffset.Now;
            TimeSpan duration = endTime - Session.StartedAt;
            if (duration < TimeSpan.Zero)
            {
                duration = TimeSpan.Zero;
            }

            return duration.TotalHours >= 1
                ? duration.ToString(@"h\:mm\:ss")
                : duration.ToString(@"mm\:ss");
        }
    }

    /// <summary>
    /// Gets a short status label for the tab so running and completed tasks are easier to scan in the tab strip.
    /// </summary>
    public string StatusText
    {
        get
        {
            if (IsApplicationLog)
            {
                return "App";
            }

            if (Session?.IsRunning == true)
            {
                return "Running";
            }

            if (Session?.Success == true)
            {
                return "Done";
            }

            if (Session?.Success == false)
            {
                return "Failed";
            }

            return "Idle";
        }
    }

    /// <summary>
    /// Gets whether the runtime tab should show a colored status marker.
    /// </summary>
    public bool HasStatusMarker => !IsApplicationLog;

    /// <summary>
    /// Gets whether the task is currently in a running state.
    /// </summary>
    public bool IsRunningStatus => Session?.IsRunning == true;

    /// <summary>
    /// Gets whether the task finished successfully.
    /// </summary>
    public bool IsSucceededStatus => Session?.IsRunning != true && Session?.Success == true;

    /// <summary>
    /// Gets whether the task finished unsuccessfully.
    /// </summary>
    public bool IsFailedStatus => Session?.IsRunning != true && Session?.Success == false;

    /// <summary>
    /// Adds a log entry to the tab and raises runtime-derived change notifications.
    /// </summary>
    public void AddLogEntry(LogEntryViewModel entry)
    {
        LogEntries.Add(entry);

        // Runtime tabs keep their own severity tallies so the selected-task header can surface diagnostics without
        // rescanning the full log stream on every refresh.
        if (entry.IsWarning)
        {
            _warningCount++;
        }

        if (entry.IsError)
        {
            _errorCount++;
        }

        RaisePropertyChanged(nameof(WarningCount));
        RaisePropertyChanged(nameof(ErrorCount));
        RaisePropertyChanged(nameof(HasWarnings));
        RaisePropertyChanged(nameof(HasErrors));
        RaisePropertyChanged(nameof(DurationText));
        RaisePropertyChanged(nameof(IsRunning));
        RaisePropertyChanged(nameof(CanTerminate));
        RaiseStatusChanged();
    }

    /// <summary>
    /// Clears the accumulated log entries for this tab.
    /// </summary>
    public void ClearLogEntries()
    {
        LogEntries.Clear();
        _warningCount = 0;
        _errorCount = 0;
        RaisePropertyChanged(nameof(WarningCount));
        RaisePropertyChanged(nameof(ErrorCount));
        RaisePropertyChanged(nameof(HasWarnings));
        RaisePropertyChanged(nameof(HasErrors));
        RaisePropertyChanged(nameof(DurationText));
        RaisePropertyChanged(nameof(IsRunning));
        RaisePropertyChanged(nameof(CanTerminate));
        RaiseStatusChanged();
    }

    /// <summary>
    /// Raises change notifications after the backing execution session changes state.
    /// </summary>
    public void NotifyStateChanged()
    {
        RaisePropertyChanged(nameof(DurationText));
        RaisePropertyChanged(nameof(IsRunning));
        RaisePropertyChanged(nameof(CanTerminate));
        RaiseStatusChanged();
    }

    /// <summary>
    /// Raises change notifications for the derived tab-status presentation properties.
    /// </summary>
    private void RaiseStatusChanged()
    {
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(HasStatusMarker));
        RaisePropertyChanged(nameof(IsRunningStatus));
        RaisePropertyChanged(nameof(IsSucceededStatus));
        RaisePropertyChanged(nameof(IsFailedStatus));
    }
}
