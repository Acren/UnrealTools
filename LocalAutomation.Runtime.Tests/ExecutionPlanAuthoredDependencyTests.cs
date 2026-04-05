using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace LocalAutomation.Runtime.Tests;

public sealed class ExecutionPlanAuthoredDependencyTests
{
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
