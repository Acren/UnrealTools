using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using LocalAutomation.Avalonia.Controls;
using LocalAutomation.Avalonia.ViewModels;

namespace LocalAutomation.Avalonia.Views.Panels;

/// <summary>
/// Hosts runtime tabs, graph selection, and termination actions while delegating log-view behavior to the reusable
/// log-viewer control.
/// </summary>
public partial class ExecutionWorkspacePanel : UserControl
{
    private Border? _graphHost;
    private Border? _logHost;
    private ExecutionWorkspaceViewModel? _observedViewModel;

    /// <summary>
    /// Initializes the runtime panel.
    /// </summary>
    public ExecutionWorkspacePanel()
    {
        InitializeComponent();
        DataContextChanged += HandleDataContextChanged;
    }

    /// <summary>
    /// Gets the runtime panel view model backing this panel.
    /// </summary>
    private ExecutionWorkspaceViewModel ViewModel => (ExecutionWorkspaceViewModel)DataContext!;

    /// <summary>
    /// Selects a runtime tab.
    /// </summary>
    private void RuntimeTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RuntimeWorkspaceTabViewModel runtimeTab })
        {
            return;
        }

        ViewModel.SelectedRuntimeTab = runtimeTab;
    }

    /// <summary>
    /// Lets users close closable runtime tabs with a middle click while keeping the permanent app log tab protected.
    /// </summary>
    private void RuntimeTab_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            return;
        }

        if (sender is not Control { DataContext: RuntimeWorkspaceTabViewModel runtimeTab } || !runtimeTab.CanClose)
        {
            return;
        }

        _ = ViewModel.CloseRuntimeTabAsync(runtimeTab);
        e.Handled = true;
    }

    /// <summary>
    /// Closes a runtime task tab from the tab strip, cancelling active work first when needed.
    /// </summary>
    private void CloseRuntimeTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RuntimeWorkspaceTabViewModel runtimeTab })
        {
            return;
        }

        _ = ViewModel.CloseRuntimeTabAsync(runtimeTab);
        e.Handled = true;
    }

    /// <summary>
    /// Cancels the current execution session when one is active.
    /// </summary>
    private async void Terminate_Click(object? sender, RoutedEventArgs e)
    {
        await ViewModel.CancelExecutionAsync();
    }

    /// <summary>
    /// Clears the currently selected runtime log from the context menu.
    /// </summary>
    private void ClearLog_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.ClearSelectedRuntimeLog();
    }

    /// <summary>
    /// Routes graph-node clicks from the embedded graph control into the runtime workspace selection model.
    /// </summary>
    private void GraphNode_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExecutionWorkspaceViewModel viewModel ||
            viewModel.SelectedRuntimeTab == null ||
            sender is not Control { DataContext: ExecutionNodeViewModel node })
        {
            return;
        }

        viewModel.SelectGraphNode(viewModel.SelectedRuntimeTab, node);
        e.Handled = true;
    }

    /// <summary>
    /// Recomputes the shared graph/log host layout whenever the selected workspace tab or panel data context changes.
    /// </summary>
    private void HandleDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_observedViewModel != null)
        {
            _observedViewModel.PropertyChanged -= HandleViewModelPropertyChanged;
        }

        _observedViewModel = DataContext as ExecutionWorkspaceViewModel;
        if (_observedViewModel != null)
        {
            _observedViewModel.PropertyChanged += HandleViewModelPropertyChanged;
        }

        UpdateWorkspaceLayout();
    }

    /// <summary>
    /// Refreshes the shared workspace layout when the selected tab or graph/log visibility changes in the view model.
    /// </summary>
    private void HandleViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.PropertyName) ||
            e.PropertyName == nameof(ExecutionWorkspaceViewModel.SelectedRuntimeTab) ||
            e.PropertyName == nameof(ExecutionWorkspaceViewModel.SelectedGraph) ||
            e.PropertyName == nameof(ExecutionWorkspaceViewModel.SelectedRuntimeLogEntries) ||
            e.PropertyName == nameof(ExecutionWorkspaceViewModel.SelectedRuntimeLogSourceId))
        {
            UpdateWorkspaceLayout();
        }
    }

    /// <summary>
    /// Applies the selected tab's graph/log presentation to the shared hosts so the panel keeps only one graph canvas
    /// and one log viewer alive while still switching between split and full-width layouts.
    /// </summary>
    private void UpdateWorkspaceLayout()
    {
        if (_graphHost == null || _logHost == null)
        {
            return;
        }

        RuntimeWorkspaceTabViewModel? selectedTab = (DataContext as ExecutionWorkspaceViewModel)?.SelectedRuntimeTab;
        bool showsGraph = selectedTab?.ShowsGraph == true;
        bool showsLog = selectedTab?.ShowsLog == true;
        bool usesSplitWorkspace = showsGraph && showsLog;

        _graphHost.IsVisible = showsGraph;
        Grid.SetColumn(_graphHost, 0);
        Grid.SetColumnSpan(_graphHost, usesSplitWorkspace ? 1 : 2);
        _graphHost.BorderThickness = usesSplitWorkspace ? new Thickness(0, 0, 1, 0) : new Thickness(0);

        _logHost.IsVisible = showsLog;
        Grid.SetColumn(_logHost, usesSplitWorkspace ? 1 : 0);
        Grid.SetColumnSpan(_logHost, usesSplitWorkspace ? 1 : 2);
    }

    /// <summary>
    /// Loads the compiled Avalonia markup for the execution workspace panel.
    /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _graphHost = this.FindControl<Border>("GraphHost");
        _logHost = this.FindControl<Border>("LogHost");
    }

}
