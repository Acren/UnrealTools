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
    /// <summary>
    /// Initializes the runtime panel.
    /// </summary>
    public ExecutionWorkspacePanel()
    {
        InitializeComponent();
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
    /// Loads the compiled Avalonia markup for the execution workspace panel.
    /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

}
