using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace LocalAutomation.Runtime.Tests;

public sealed class ExecutionPlanBuilderFluentTests
{
    /// <summary>
    /// Confirms that fluent Then(...) chaining inside a sequenced scope depends on the previous authored task node.
    /// </summary>
    [Fact]
    public void ThenInSequencedScopeDependsOnPreviousAuthoredTask()
    {
        // Arrange: author the same fluent pattern used by production deploy steps.
        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(steps => steps
                .Task("Prepare Workspace")
                    .Run(() => Task.CompletedTask)
                .Then("Stage Plugin")
                    .Run(() => Task.CompletedTask));
        });

        // Act: build the authored plan and inspect the visible sibling tasks in the chain.
        ExecutionPlan plan = RuntimeTestUtilities.BuildPlan(operation);
        ExecutionTask prepareWorkspace = plan.Tasks.Single(task => task.Title == "Prepare Workspace");
        ExecutionTask stagePlugin = plan.Tasks.Single(task => task.Title == "Stage Plugin");

        // Assert: Stage Plugin should depend on the previous authored task node.
        Assert.Contains(prepareWorkspace.Id, plan.GetTaskDependencies(stagePlugin.Id));
    }

    /// <summary>
    /// Confirms that a longer fluent Then(...) chain keeps advancing through each previous authored sibling task.
    /// </summary>
    [Fact]
    public void ThenChainKeepsAdvancingThroughPreviousAuthoredSibling()
    {
        // Arrange: author the same three-step fluent sibling chain used in production deployment flows.
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

        // Act: build the plan and inspect the visible tasks in the authored chain.
        ExecutionPlan plan = RuntimeTestUtilities.BuildPlan(operation);
        ExecutionTask prepareWorkspace = plan.Tasks.Single(task => task.Title == "Prepare Workspace");
        ExecutionTask stagePlugin = plan.Tasks.Single(task => task.Title == "Stage Plugin");
        ExecutionTask buildEditor = plan.Tasks.Single(task => task.Title == "Build Editor");

        // Assert: each fluent sibling should depend on the previous authored sibling task.
        Assert.Contains(prepareWorkspace.Id, plan.GetTaskDependencies(stagePlugin.Id));
        Assert.Contains(stagePlugin.Id, plan.GetTaskDependencies(buildEditor.Id));
    }
}
