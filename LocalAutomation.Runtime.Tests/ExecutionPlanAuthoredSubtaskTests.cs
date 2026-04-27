using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using TestUtilities;
using Xunit;

namespace LocalAutomation.Runtime.Tests;

public sealed class ExecutionPlanAuthoredSubtaskTests
{
    /// <summary>
    /// Confirms that explicit authored subtasks preserve the same declaration order when body work and nested child
    /// scopes need to be interleaved.
    /// </summary>
    [Fact]
    public async Task ExplicitSubtasksKeepDeclaredExecutionOrder()
    {
        // Arrange: record each body as it runs so the final list reflects scheduler start order directly.
        List<string> executionOrder = new();
        object executionOrderLock = new();

        void Record(string step)
        {
            lock (executionOrderLock)
            {
                executionOrder.Add(step);
            }
        }

        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(scope =>
            {
                scope.Task("Root Body 1").Run(() =>
                {
                    Record("root-body-1");
                    return Task.CompletedTask;
                })
                .Then("Child").Run(() =>
                {
                    Record("child-body");
                    return Task.CompletedTask;
                })
                .Then("Root Body 2").Run(() =>
                {
                    Record("root-body-2");
                    return Task.CompletedTask;
                })
                .Then("Nested").Children(nestedScope =>
                {
                    nestedScope.Task("Nested Body").Run(() =>
                    {
                        Record("nested-body");
                        return Task.CompletedTask;
                    });
                });
            });
        });

        // Act: execute the authored plan through the real runtime pipeline.
        (_, _, OperationResult result) = await RuntimeTestUtilities.ExecuteAsync(operation);

        // Assert: the supported explicit-subtask model should preserve authored execution order exactly.
        Assert.Equal(ExecutionTaskOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "root-body-1", "child-body", "root-body-2", "nested-body" }, executionOrder);
    }
}
