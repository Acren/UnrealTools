using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace LocalAutomation.Runtime.Tests;

public sealed class ExecutionPlanBuilderFluentTests
{
    /// <summary>
    /// Confirms that fluent Then(...) chaining inside a sequenced scope follows the previous task's completion frontier
    /// instead of just reusing the parent task as ambient context.
    /// </summary>
    [Fact]
    public void ThenInSequencedScopeDependsOnPreviousTaskCompletionFrontier()
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

        // Act: build the authored plan and find the visible tasks plus the hidden body task they sequence through.
        ExecutionPlan plan = RuntimeTestUtilities.BuildPlan(operation);
        ExecutionTask prepareWorkspace = plan.Tasks.Single(task => task.Title == "Prepare Workspace");
        ExecutionTask prepareWorkspaceBody = plan.Tasks.Single(task => task.Title == "Prepare Workspace.Body" && task.ParentId == prepareWorkspace.Id);
        ExecutionTask stagePlugin = plan.Tasks.Single(task => task.Title == "Stage Plugin");

        // Assert: Stage Plugin should wait for Prepare Workspace's body task, not float independently under the same parent.
        Assert.Contains(prepareWorkspaceBody.Id, plan.GetTaskDependencies(stagePlugin.Id));
    }

    /// <summary>
    /// Confirms that a longer fluent Then(...) chain keeps advancing through each previous sibling's completion frontier
    /// instead of repeatedly attaching every later sibling to the same parent-level state.
    /// </summary>
    [Fact]
    public void ThenChainKeepsAdvancingThroughPreviousSiblingCompletion()
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

        // Act: build the plan and find each visible task plus the hidden body tasks that represent their real completion.
        ExecutionPlan plan = RuntimeTestUtilities.BuildPlan(operation);
        ExecutionTask prepareWorkspace = plan.Tasks.Single(task => task.Title == "Prepare Workspace");
        ExecutionTask prepareWorkspaceBody = plan.Tasks.Single(task => task.Title == "Prepare Workspace.Body" && task.ParentId == prepareWorkspace.Id);
        ExecutionTask stagePlugin = plan.Tasks.Single(task => task.Title == "Stage Plugin");
        ExecutionTask stagePluginBody = plan.Tasks.Single(task => task.Title == "Stage Plugin.Body" && task.ParentId == stagePlugin.Id);
        ExecutionTask buildEditor = plan.Tasks.Single(task => task.Title == "Build Editor");

        // Assert: each fluent sibling should wait for the previous sibling's real completion frontier.
        Assert.Contains(prepareWorkspaceBody.Id, plan.GetTaskDependencies(stagePlugin.Id));
        Assert.Contains(stagePluginBody.Id, plan.GetTaskDependencies(buildEditor.Id));
    }
}
