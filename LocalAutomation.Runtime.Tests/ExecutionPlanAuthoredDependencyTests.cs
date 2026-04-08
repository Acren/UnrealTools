using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace LocalAutomation.Runtime.Tests;

public sealed class ExecutionPlanAuthoredDependencyTests
{
    /// <summary>
    /// Confirms that Run(...) attaches executable work directly to the visible authored task instead of synthesizing a
    /// hidden body subtask beneath it.
    /// </summary>
    [Fact]
    public void RunDoesNotCreateHiddenBodySubtaskBeneathVisibleTask()
    {
        // Arrange: author one visible task with a body so the resulting plan should contain only the authored task node.
        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(steps => steps
                .Task("Prepare Workspace")
                    .Run(() => Task.CompletedTask));
        });

        // Act: build the authored plan and locate the visible task plus any descendants currently attached beneath it.
        ExecutionPlan plan = RuntimeTestUtilities.BuildPlan(operation);
        ExecutionTask prepareWorkspace = plan.Tasks.Single(task => task.Title == "Prepare Workspace");
        ExecutionTask[] prepareWorkspaceChildren = plan.Tasks.Where(task => task.ParentId == prepareWorkspace.Id).ToArray();

        // Assert: the authored task stays visible and no synthetic hidden body child is created for plain Run(...).
        Assert.False(prepareWorkspace.IsHiddenInGraph);
        Assert.Empty(prepareWorkspaceChildren);
    }

    /// <summary>
    /// Confirms that asking the session to start the visible authored task executes that same task directly instead of
    /// delegating to a hidden body child.
    /// </summary>
    [Fact]
    public async Task StartingVisibleTaskExecutesThatSameTask()
    {
        // Arrange: build one visible task with body work and create the live runtime session.
        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(steps => steps
                .Task("Prepare Workspace")
                    .Run(() => Task.FromResult(OperationResult.Succeeded())));
        });

        (ExecutionPlan plan, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        ExecutionTask prepareWorkspace = plan.Tasks.Single(task => task.Title == "Prepare Workspace");
        TaskStartResult startResult = session.StartTaskAsync(
            prepareWorkspace.Id,
            _ => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            default,
            scheduler,
            (_, executeAsync) => executeAsync());

        // Act: start the visible task id and await the actual running task it returns.
        OperationResult result = await startResult.RunningTask;

        // Assert: session start keeps the authored task as the execution identity.
        Assert.Equal(prepareWorkspace.Id, startResult.Task.Id);
        Assert.Equal(prepareWorkspace.Id, startResult.ExecutionTask.Id);
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
