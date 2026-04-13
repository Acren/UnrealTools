using System.ComponentModel;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LocalAutomation.Application;
using LocalAutomation.Avalonia.ViewModels;
using LocalAutomation.Core;
using LocalAutomation.Runtime;
using TestUtilities;
using RuntimeExecutionTask = LocalAutomation.Runtime.ExecutionTask;
using RuntimeExecutionTaskId = LocalAutomation.Runtime.ExecutionTaskId;
using RuntimeExecutionTaskState = LocalAutomation.Runtime.ExecutionTaskState;
using Xunit;

namespace LocalAutomation.Avalonia.Tests;

/// <summary>
/// Covers the execution-workspace task-state batching behavior that fans out into selected log refreshes.
/// </summary>
public sealed class ExecutionWorkspaceViewModelTests
{
    /// <summary>
    /// Proves that one queued batch of descendant state changes should coalesce into at most one selected-log refresh for
    /// the currently selected container node.
    /// </summary>
    [AvaloniaFact]
    public void BatchedTaskStateFlushShouldRefreshSelectedLogEntriesAtMostOnce()
    {
        /* Initialize the shared logger explicitly so any background runtime paths touched by the workspace reuse the same
           test-host logging setup as the existing runtime and application suites. */
        _ = TestLoggingBootstrap.LoggerFactory;

        /* Build one real workspace plus one attached execution session so the test exercises the same graph selection,
           selected-log rebuild, and metrics refresh wiring used by the shell. */
        LocalAutomationApplicationHost services = LocalAutomationApplicationHost.Create();
        ExecutionWorkspaceViewModel workspace = new(services, _ => { });

        /* Keep the authored plan minimal while still giving the workspace one selected container and several descendants
           that can participate in one coalesced task-state batch. */
        ExecutionTestCommon.InlineOperation operation = new(
            root =>
            {
                root.Children(children =>
                {
                    children.Task("First Child").Run(() => Task.CompletedTask);
                    children.Task("Second Child").Run(() => Task.CompletedTask);
                    children.Task("Third Child").Run(() => Task.CompletedTask);
                });
            },
            operationName: "Workspace Task-State Batch Test");
        Operation nonGenericOperation = operation;
        OperationParameters parameters = nonGenericOperation.CreateParameters();
        parameters.Target = new ExecutionTestCommon.TestTarget();
        ExecutionPlan plan = ExecutionPlanFactory.BuildPlan(nonGenericOperation, parameters)
            ?? throw new InvalidOperationException("The workspace batching test operation did not produce an execution plan.");
        ExecutionSession session = new(new BufferedLogStream(), plan);
        workspace.AttachExecutionSession(session);

        RuntimeWorkspaceTabViewModel runtimeTab = Assert.Single(workspace.RuntimeTabs, tab => tab.Kind == RuntimeWorkspaceTabKind.ExecutionSession);
        RuntimeExecutionTask rootTask = Assert.Single(session.Tasks, task => task.ParentId == null);
        ExecutionNodeViewModel selectedNode = Assert.Single(runtimeTab.Graph.Nodes, node => node.Id == rootTask.Id);
        Assert.True(selectedNode.IsContainer);

        /* Select the root container first so every descendant in the pending batch targets the current selected subtree. */
        workspace.SelectGraphNode(runtimeTab, selectedNode);

        int selectedLogEntriesChangedCount = 0;
        runtimeTab.PropertyChanged += HandleRuntimeTabPropertyChanged;
        try
        {
            /* Seed one pending flush with multiple changed descendants. The current implementation rebuilds the selected
               log entries once per task inside that single flush, so this assertion should stay red until batching is
               tightened to one selected-log refresh at most. */
            IReadOnlyList<RuntimeExecutionTaskId> pendingTaskIds = session.Tasks
                .Where(task => task.ParentId == rootTask.Id)
                .Select(task => task.Id)
                .Take(3)
                .ToList();
            Assert.Equal(3, pendingTaskIds.Count);

            /* Drive the real public session event path so the regression depends only on production observer wiring:
               task-state change -> workspace batch queue -> dispatcher-posted flush -> selected-log refresh. */
            foreach (RuntimeExecutionTaskId pendingTaskId in pendingTaskIds)
            {
                session.SetTaskState(pendingTaskId, RuntimeExecutionTaskState.AwaitingDependency);
            }

            /* Drain the queued workspace callbacks on the Avalonia dispatcher so the assertion observes the fully applied
               batched update rather than the pre-dispatch intermediate state. */
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            runtimeTab.PropertyChanged -= HandleRuntimeTabPropertyChanged;
        }

        Assert.True(
            selectedLogEntriesChangedCount <= 1,
            $"Expected at most one SelectedLogEntries refresh for one task-state batch, but observed {selectedLogEntriesChangedCount}.");

        /* Count only the tab-level selected-log collection replacements triggered by the batched task-state flush. */
        void HandleRuntimeTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, nameof(RuntimeWorkspaceTabViewModel.SelectedLogEntries), StringComparison.Ordinal))
            {
                selectedLogEntriesChangedCount++;
            }
        }
    }
}
