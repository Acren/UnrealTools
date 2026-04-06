using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace LocalAutomation.Runtime.Tests;

public sealed class ExecutionPlanParallelJoinTests
{
    /// <summary>
    /// Confirms that one authored task can explicitly join two earlier sibling branches without falling back to fluent
    /// sequencing through only the most recently declared branch.
    /// </summary>
    [Fact]
    public void TaskCanJoinMultipleParallelBranchesWithAfter()
    {
        // Arrange: author the same branch-and-join shape used by the deploy-plugin DAG refactor.
        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                ExecutionTaskBuilder left = scope.Task("Left")
                    .Run(() => Task.CompletedTask);
                ExecutionTaskBuilder right = scope.Task("Right")
                    .Run(() => Task.CompletedTask);
                scope.Task("Join")
                    .After(left.Id, right.Id)
                    .Run(() => Task.CompletedTask);
            });
        });

        // Act: build the authored plan and locate the visible branch and join tasks.
        ExecutionPlan plan = RuntimeTestUtilities.BuildPlan(operation);
        ExecutionTask left = plan.Tasks.Single(task => task.Title == "Left");
        ExecutionTask right = plan.Tasks.Single(task => task.Title == "Right");
        ExecutionTask join = plan.Tasks.Single(task => task.Title == "Join");

        // Assert: the join should wait on both visible branch tasks.
        Assert.Contains(left.Id, plan.GetTaskDependencies(join.Id));
        Assert.Contains(right.Id, plan.GetTaskDependencies(join.Id));
    }
}
