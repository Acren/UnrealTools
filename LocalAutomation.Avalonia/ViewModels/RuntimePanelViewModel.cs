using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using LocalAutomation.Core;
using LocalAutomationApplicationHost = LocalAutomation.Application.LocalAutomationApplicationHost;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Owns the runtime panel's tabs, selected-task header state, and task lifecycle actions so the shell view model does
/// not need to carry panel-specific UI behavior.
/// </summary>
public sealed class RuntimePanelViewModel : ViewModelBase
{
    private readonly LocalAutomationApplicationHost _services;
    private readonly Action<string> _setStatus;
    private readonly DispatcherTimer _runtimeDurationTimer;
    private RuntimeTaskTabViewModel? _observedRuntimeTab;
    private RuntimeTaskTabViewModel? _selectedRuntimeTab;
    private event PropertyChangedEventHandler? _selectedRuntimeTabPropertyChanged;

    /// <summary>
    /// Creates the runtime panel view model around the shared runtime services and shell status sink.
    /// </summary>
    public RuntimePanelViewModel(LocalAutomationApplicationHost services, Action<string> setStatus)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _runtimeDurationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _runtimeDurationTimer.Tick += HandleRuntimeDurationTimerTick;

        RuntimeTabs.Add(CreateApplicationLogTab());
        SelectedRuntimeTab = RuntimeTabs[0];
        AttachApplicationLogStream();
    }

    /// <summary>
    /// Gets the runtime tabs shown in the panel. The first tab is the permanent application log.
    /// </summary>
    public ObservableCollection<RuntimeTaskTabViewModel> RuntimeTabs { get; } = new();

    /// <summary>
    /// Gets or sets the runtime tab currently selected by the panel.
    /// </summary>
    public RuntimeTaskTabViewModel? SelectedRuntimeTab
    {
        get => _selectedRuntimeTab;
        set
        {
            if (!SetProperty(ref _selectedRuntimeTab, value))
            {
                return;
            }

            // Selected-task header pills bind through this panel view model, so selected-tab property changes need to
            // be mirrored into the runtime panel's derived properties.
            if (_selectedRuntimeTabPropertyChanged != null)
            {
                if (_observedRuntimeTab != null)
                {
                    _observedRuntimeTab.PropertyChanged -= _selectedRuntimeTabPropertyChanged;
                }

                _selectedRuntimeTabPropertyChanged = null;
                _observedRuntimeTab = null;
            }

            if (value != null)
            {
                _selectedRuntimeTabPropertyChanged = (_, args) => HandleSelectedRuntimeTabPropertyChanged(args.PropertyName);
                value.PropertyChanged += _selectedRuntimeTabPropertyChanged;
                _observedRuntimeTab = value;
            }

            foreach (RuntimeTaskTabViewModel tab in RuntimeTabs)
            {
                tab.IsSelected = ReferenceEquals(tab, value);
            }

            RaiseSelectionStateChanged();
        }
    }

    /// <summary>
    /// Gets whether the selected runtime tab currently represents active work that can be terminated.
    /// </summary>
    public bool IsRunning => SelectedRuntimeTab?.CanTerminate == true;

    /// <summary>
    /// Gets a short execution summary line for the selected runtime tab.
    /// </summary>
    public string ExecutionSummary
    {
        get
        {
            if (SelectedRuntimeTab == null)
            {
                return "No runtime tab is selected.";
            }

            if (SelectedRuntimeTab.IsApplicationLog)
            {
                return "Application log messages appear here even when no tasks are running.";
            }

            if (SelectedRuntimeTab.Session?.IsRunning == true)
            {
                return $"Running {SelectedRuntimeTab.Session.OperationName} on {SelectedRuntimeTab.Session.TargetName}";
            }

            if (SelectedRuntimeTab.Session?.Success == true)
            {
                return $"{SelectedRuntimeTab.Session.OperationName} succeeded for {SelectedRuntimeTab.Session.TargetName}";
            }

            if (SelectedRuntimeTab.Session != null)
            {
                return $"{SelectedRuntimeTab.Session.OperationName} failed for {SelectedRuntimeTab.Session.TargetName}";
            }

            return SelectedRuntimeTab.Subtitle;
        }
    }

    /// <summary>
    /// Gets the log entries displayed for the currently selected runtime tab.
    /// </summary>
    public ObservableCollection<LogEntryViewModel> SelectedRuntimeLogEntries => SelectedRuntimeTab?.LogEntries ?? new ObservableCollection<LogEntryViewModel>();

    /// <summary>
    /// Gets the selected runtime-tab title shown in the panel header.
    /// </summary>
    public string SelectedRuntimeTabTitle => SelectedRuntimeTab?.Title ?? "Runtime";

    /// <summary>
    /// Gets whether the selected runtime tab should show task metrics in the header.
    /// </summary>
    public bool ShowSelectedRuntimeMetrics => SelectedRuntimeTab is { IsApplicationLog: false };

    /// <summary>
    /// Gets the warning count for the selected runtime task.
    /// </summary>
    public int SelectedRuntimeWarningCount => SelectedRuntimeTab?.WarningCount ?? 0;

    /// <summary>
    /// Gets the error count for the selected runtime task.
    /// </summary>
    public int SelectedRuntimeErrorCount => SelectedRuntimeTab?.ErrorCount ?? 0;

    /// <summary>
    /// Gets the elapsed duration text for the selected runtime task.
    /// </summary>
    public string SelectedRuntimeDuration => SelectedRuntimeTab?.DurationText ?? "--:--";

    /// <summary>
    /// Adds a newly started execution session to the runtime panel and begins mirroring its logs and completion state.
    /// </summary>
    public void AttachExecutionSession(ExecutionSession session)
    {
        if (session == null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        RuntimeTaskTabViewModel runtimeTab = new(
            id: session.Id,
            title: session.OperationName,
            subtitle: session.TargetName,
            isApplicationLog: false,
            session: session);

        RuntimeTabs.Add(runtimeTab);
        SelectedRuntimeTab = runtimeTab;
        if (!_runtimeDurationTimer.IsEnabled)
        {
            _runtimeDurationTimer.Start();
        }

        // Execution can start logging immediately on a background thread before the UI has attached the tab. Seed the
        // tab from the current buffered entries first so fast tasks still show their full history instead of only the
        // tail that arrived after subscription.
        foreach (LogEntry entry in session.LogStream.Entries)
        {
            runtimeTab.AddLogEntry(new LogEntryViewModel(entry.Message, entry.Verbosity));
        }

        session.LogStream.EntryAdded += entry => Dispatcher.UIThread.Post(() =>
        {
            runtimeTab.AddLogEntry(new LogEntryViewModel(entry.Message, entry.Verbosity));
            if (ReferenceEquals(SelectedRuntimeTab, runtimeTab))
            {
                RaiseSelectionStateChanged();
            }
        });

        _ = WatchExecutionCompletionAsync(runtimeTab);
        RaiseSelectionStateChanged();
    }

    /// <summary>
    /// Cancels the selected execution session when one is currently running.
    /// </summary>
    public async Task CancelExecutionAsync()
    {
        if (SelectedRuntimeTab?.Session is not { IsRunning: true } session)
        {
            return;
        }

        await session.CancelAsync();
        _setStatus($"Cancelling {session.OperationName}.");
        RaiseSelectionStateChanged();
    }

    /// <summary>
    /// Clears the log entries for the selected runtime tab.
    /// </summary>
    public void ClearSelectedRuntimeLog()
    {
        if (SelectedRuntimeTab == null)
        {
            return;
        }

        SelectedRuntimeTab.ClearLogEntries();
        _setStatus($"Cleared log output for {SelectedRuntimeTab.Title.ToLowerInvariant()}.");
        RaiseSelectionStateChanged();
    }

    /// <summary>
    /// Closes the provided runtime tab when it is not the permanent app log tab. Running tabs are cancelled first so
    /// the close affordance does not leave background work orphaned after the tab disappears.
    /// </summary>
    public async Task CloseRuntimeTabAsync(RuntimeTaskTabViewModel? runtimeTab)
    {
        if (runtimeTab == null || !runtimeTab.CanClose)
        {
            return;
        }

        // Closing a running tab should behave like terminate-and-dismiss so the session is asked to stop before it is
        // removed from the visible runtime collection.
        if (runtimeTab.Session is { IsRunning: true } session)
        {
            await session.CancelAsync();
            _setStatus($"Cancelling {session.OperationName} before closing its tab.");
        }

        RemoveRuntimeTab(runtimeTab);
    }

    /// <summary>
    /// Polls the running session until it completes so completion state can be reflected back into the runtime panel.
    /// </summary>
    private async Task WatchExecutionCompletionAsync(RuntimeTaskTabViewModel runtimeTab)
    {
        ExecutionSession session = runtimeTab.Session!;
        while (session.IsRunning)
        {
            await Task.Delay(100);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            session.FinishedAt = DateTimeOffset.Now;
            runtimeTab.NotifyStateChanged();
            if (ReferenceEquals(SelectedRuntimeTab, runtimeTab))
            {
                RaiseSelectionStateChanged();
            }

            if (session.Success == true)
            {
                _setStatus($"{session.OperationName} succeeded for {session.TargetName}.");
            }
            else
            {
                _setStatus($"{session.OperationName} finished with failure for {session.TargetName}.");
            }
        });
    }

    /// <summary>
    /// Removes a runtime tab from the visible collection and picks the next selected tab.
    /// </summary>
    private void RemoveRuntimeTab(RuntimeTaskTabViewModel runtimeTab)
    {
        int removedIndex = RuntimeTabs.IndexOf(runtimeTab);
        RuntimeTabs.Remove(runtimeTab);

        // Once the UI tab is dismissed, drop the backing session from the shared session list so shell state and any
        // persisted execution history only track tabs that remain visible.
        if (runtimeTab.Session != null)
        {
            _services.Execution.RemoveSession(runtimeTab.Session.Id);
        }

        if (ReferenceEquals(SelectedRuntimeTab, runtimeTab))
        {
            int nextIndex = Math.Clamp(removedIndex - 1, 0, RuntimeTabs.Count - 1);
            SelectedRuntimeTab = RuntimeTabs.Count > 0 ? RuntimeTabs[nextIndex] : null;
        }

        RaiseSelectionStateChanged();
    }

    /// <summary>
    /// Seeds the runtime panel with application-level log entries and keeps listening so startup or crash details stay
    /// visible even when no task is running.
    /// </summary>
    private void AttachApplicationLogStream()
    {
        RuntimeTaskTabViewModel applicationTab = RuntimeTabs[0];
        foreach (LogEntry entry in ApplicationLogService.LogStream.Entries)
        {
            applicationTab.AddLogEntry(new LogEntryViewModel(entry.Message, entry.Verbosity));
        }

        ApplicationLogService.LogStream.EntryAdded += entry => Dispatcher.UIThread.Post(() =>
        {
            applicationTab.AddLogEntry(new LogEntryViewModel(entry.Message, entry.Verbosity));
            if (ReferenceEquals(SelectedRuntimeTab, applicationTab))
            {
                RaiseSelectionStateChanged();
            }
        });
    }

    /// <summary>
    /// Refreshes the selected task duration once per second while any runtime session remains active.
    /// </summary>
    private void HandleRuntimeDurationTimerTick(object? sender, EventArgs e)
    {
        RaisePropertyChanged(nameof(SelectedRuntimeDuration));

        if (!RuntimeTabs.Any(tab => tab.IsRunning))
        {
            _runtimeDurationTimer.Stop();
        }
    }

    /// <summary>
    /// Mirrors selected runtime-tab property changes into the derived shell properties used by the panel header.
    /// </summary>
    private void HandleSelectedRuntimeTabPropertyChanged(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            RaiseSelectionStateChanged();
            return;
        }

        switch (propertyName)
        {
            case nameof(RuntimeTaskTabViewModel.DurationText):
                RaisePropertyChanged(nameof(SelectedRuntimeDuration));
                break;
            default:
                RaiseSelectionStateChanged();
                break;
        }
    }

    /// <summary>
    /// Raises change notifications for all derived state shown by the runtime panel.
    /// </summary>
    private void RaiseSelectionStateChanged()
    {
        RaisePropertyChanged(nameof(ExecutionSummary));
        RaisePropertyChanged(nameof(IsRunning));
        RaisePropertyChanged(nameof(SelectedRuntimeDuration));
        RaisePropertyChanged(nameof(SelectedRuntimeErrorCount));
        RaisePropertyChanged(nameof(SelectedRuntimeLogEntries));
        RaisePropertyChanged(nameof(SelectedRuntimeTabTitle));
        RaisePropertyChanged(nameof(SelectedRuntimeWarningCount));
        RaisePropertyChanged(nameof(ShowSelectedRuntimeMetrics));
    }

    /// <summary>
    /// Creates the permanent first runtime tab used for application-level logs.
    /// </summary>
    private static RuntimeTaskTabViewModel CreateApplicationLogTab()
    {
        return new RuntimeTaskTabViewModel(
            id: "application-log",
            title: "App Log",
            subtitle: string.Empty,
            isApplicationLog: true);
    }
}
