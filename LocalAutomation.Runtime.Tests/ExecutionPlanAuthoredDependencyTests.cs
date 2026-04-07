using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace LocalAutomation.Runtime.Tests;

public sealed class ExecutionPlanAuthoredDependencyTests
{
    /// <summary>
    /// Confirms that Run(...) creates a separate hidden body subtask beneath the visible authored task.
    /// </summary>
    [Fact]
    public void RunCreatesHiddenBodySubtaskBeneathVisibleTask()
    {
        // Arrange: author one visible task with a body so the resulting plan contains both the visible step and the
        // hidden body subtask that actually runs the work.
        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(steps => steps
                .Task("Prepare Workspace")
                    .Run(() => Task.CompletedTask));
        });

        // Act: build the authored plan and locate the visible task plus its generated body subtask.
        ExecutionPlan plan = RuntimeTestUtilities.BuildPlan(operation);
        ExecutionTask prepareWorkspace = plan.Tasks.Single(task => task.Title == "Prepare Workspace");
        ExecutionTask prepareWorkspaceBody = plan.Tasks.Single(task => task.Title == "Prepare Workspace.Body" && task.ParentId == prepareWorkspace.Id);

        // Assert: the body is modeled as a normal hidden child task rather than as a separate public task kind.
        Assert.False(prepareWorkspace.IsHiddenInGraph);
        Assert.True(prepareWorkspaceBody.IsHiddenInGraph);
    }

    /// <summary>
    /// Confirms that asking the session to start the visible authored task delegates to the hidden body child that holds
    /// the actual runnable work.
    /// </summary>
    [Fact]
    public async Task StartingVisibleTaskDelegatesToHiddenBodyChild()
    {
        // Arrange: build one visible task with hidden body work and create the live runtime session.
        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(steps => steps
                .Task("Prepare Workspace")
                    .Run(() => Task.FromResult(OperationResult.Succeeded())));
        });

        (ExecutionPlan plan, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        ExecutionTask prepareWorkspace = plan.Tasks.Single(task => task.Title == "Prepare Workspace");
        ExecutionTask prepareWorkspaceBody = plan.Tasks.Single(task => task.Title == "Prepare Workspace.Body" && task.ParentId == prepareWorkspace.Id);
        TaskStartResult startResult = session.StartTaskAsync(
            prepareWorkspace.Id,
            _ => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            default,
            scheduler,
            (_, executeAsync) => executeAsync());

        // Act: start the visible task id and await the actual running task it delegated to.
        OperationResult result = await startResult.RunningTask;

        // Assert: session start delegation picks the hidden body child as the actual started task.
        Assert.Equal(prepareWorkspaceBody.Id, startResult.Task.Id);
        Assert.Equal(ExecutionTaskOutcome.Completed, result.Outcome);
    }

    /// <summary>
    /// Confirms that fluent sibling chaining records dependencies on the previous authored task node instead of hidden
    /// implementation details such as body tasks.
    /// </summary>
    [Fact]
    public void ThenDependsOnPreviousAuthoredTask()
    {
        // Arrange: author the same visible task chain used by production deployment flows.
        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(steps => steps
                .Task("Prepare Workspace")
                    .Run(() => Task.CompletedTask)
                .Then("Stage Plugin")
                    .Run(() => Task.CompletedTask)
                .Then("Build Editor")
                    .Run(() => Task.CompletedTask));
        });

        // Act: build the authored plan and inspect only the visible task nodes in the chain.
        ExecutionPlan plan = RuntimeTestUtilities.BuildPlan(operation);
        ExecutionTask prepareWorkspace = plan.Tasks.Single(task => task.Title == "Prepare Workspace");
        ExecutionTask stagePlugin = plan.Tasks.Single(task => task.Title == "Stage Plugin");
        ExecutionTask buildEditor = plan.Tasks.Single(task => task.Title == "Build Editor");

        // Assert: authored siblings should depend on the previous authored task node, not on hidden body-task nodes.
        Assert.Contains(prepareWorkspace.Id, plan.GetTaskDependencies(stagePlugin.Id));
        Assert.Contains(stagePlugin.Id, plan.GetTaskDependencies(buildEditor.Id));
    }
}
