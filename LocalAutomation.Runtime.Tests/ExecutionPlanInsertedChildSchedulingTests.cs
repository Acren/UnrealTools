using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TestUtilities;
using Xunit;

namespace LocalAutomation.Runtime.Tests;

public sealed class ExecutionPlanInsertedChildSchedulingTests
{
    /// <summary>
    /// Confirms that live child-operation insertion should not break scheduler traversal while another branch keeps the
    /// runtime graph active.
    /// </summary>
    [Fact]
    public async Task LiveChildInsertionShouldNotBreakSchedulerTraversal()
    {
        /* Keep one sibling visibly active while the other sibling repeatedly inserts tiny child operations. The current
           race throws from ExecutionTask child-list traversal when scheduler reads and merge-time writes overlap. */
        TaskCompletionSource<bool> companionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseCompanion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        const int insertedChildOperationCount = 200;

        var operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                /* This branch exists only to keep the scheduler actively evaluating the live graph while the other branch
                   grows that same graph through repeated child-operation merges. */
                scope.Task("Traversal Companion").Run(async _ =>
                {
                    companionStarted.TrySetResult(true);
                    await releaseCompanion.Task.WaitAsync(TimeSpan.FromSeconds(5));
                });

                scope.Task("Insert Child Operations").Run(async context =>
                {
                    /* Repeated short child operations amplify the original production race without adding any synthetic
                       test-only mutation hooks. Each iteration takes the normal BuildPlan -> MergeChildTasks -> wait path. */
                    for (int index = 0; index < insertedChildOperationCount; index += 1)
                    {
                        var childOperation = new ExecutionTestCommon.InlineOperation(
                            childRoot =>
                            {
                                childRoot.Run(async _ =>
                                {
                                    await Task.Yield();
                                });
                            },
                            operationName: $"Inserted Child Operation {index}");

                        OperationParameters childParameters = childOperation.CreateParameters(context.ValidatedOperationParameters.CreateChild());
                        OperationResult childResult = await context.RunChildOperationAsync((Operation)childOperation, childParameters);
                        if (!childResult.Success)
                        {
                            throw new InvalidOperationException(childResult.FailureReason ?? $"Inserted child operation {index} failed.");
                        }
                    }
                });
            });
        });

        (ExecutionPlan _, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime((Operation)operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);

        try
        {
            /* Wait until the companion branch is definitely active so child-operation insertion overlaps a live scheduler
               run rather than completing during startup before scheduler traversal begins in earnest. */
            await companionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseCompanion.TrySetResult(true);

            /* This should complete cleanly. Before the traversal fix, this path can fault with "Collection was modified;
               enumeration operation may not execute." when child merges append while the scheduler is walking _children. */
            OperationResult result = await executeTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ExecutionTaskOutcome.Completed, result.Outcome);
        }
        finally
        {
            releaseCompanion.TrySetResult(true);
        }
    }

    /// <summary>
    /// Confirms that when two sibling branches become ready together, a lock-blocked inserted child operation beneath the
    /// first sibling does not prevent the second independently ready sibling from starting.
    /// </summary>
    [Fact]
    public async Task ReadySiblingStartsEvenWhenEarlierSiblingInsertedChildIsLockBlocked()
    {
        /* Keep the inserted child operation lock-blocked and hold the second sibling body open so the assertion observes
           the scheduler state while both branches should still be active. */
        ExecutionLock sharedLock = new("sibling-fairness-lock");
        TaskCompletionSource<bool> releaseLockHolder = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseSecondSibling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> lockHolderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> secondSiblingStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId secondSiblingVisibleTaskId = default;
        ExecutionTaskId insertedChildRootTaskId = default;

        var operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, rootScope =>
            {
                rootScope.Task("Lock Holder")
                    .Run(async context =>
                    {
                        /* Hold the shared execution lock through a child operation so the first sibling's inserted child
                           operation remains pending while the second sibling is already independently ready. */
                        var lockHolderOperation = new ExecutionTestCommon.InlineOperation(
                            lockHolderRoot =>
                            {
                                lockHolderRoot.Run(async _ =>
                                {
                                    lockHolderStarted.TrySetResult(true);
                                    await releaseLockHolder.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                });
                            },
                            operationName: "Lock Holder Child Operation",
                            executionLocks: new[] { sharedLock });

                        OperationParameters lockHolderParameters = lockHolderOperation.CreateParameters(context.ValidatedOperationParameters.CreateChild());
                        OperationResult lockHolderResult = await context.RunChildOperationAsync((Operation)lockHolderOperation, lockHolderParameters);
                        if (!lockHolderResult.Success)
                        {
                            throw new InvalidOperationException(lockHolderResult.FailureReason ?? "Lock holder child operation failed.");
                        }
                    });

                rootScope.Task("Sequence")
                    .Children(sequence =>
                    {
                        sequence.Task("Prepare")
                            .Run(() => Task.CompletedTask);

                        sequence.Task("Parallel Work")
                            .Children(ExecutionChildMode.Parallel, parallel =>
                            {
                                parallel.Task("First Sibling")
                                    .Run(async context =>
                                    {
                                        /* Insert one child operation whose body would require the same shared lock held by
                                           the separate lock-holder branch. This leaves the inserted child root pending while
                                           the second sibling should still be free to start. */
                                        var childOperation = new ExecutionTestCommon.InlineOperation(
                                            childRoot =>
                                            {
                                                insertedChildRootTaskId = childRoot.Id;
                                                childRoot.Run(() => Task.CompletedTask);
                                            },
                                            operationName: "Inserted Child Operation",
                                            executionLocks: new[] { sharedLock });

                                        OperationParameters childParameters = childOperation.CreateParameters(context.ValidatedOperationParameters.CreateChild());
                                        OperationResult childResult = await context.RunChildOperationAsync((Operation)childOperation, childParameters);
                                        if (!childResult.Success)
                                        {
                                            throw new InvalidOperationException(childResult.FailureReason ?? "Inserted child operation failed.");
                                        }
                                    });

                                ExecutionTaskBuilder secondSibling = parallel.Task("Second Sibling");
                                secondSiblingVisibleTaskId = secondSibling.Id;
                                secondSibling.Run(async _ =>
                                {
                                    secondSiblingStarted.TrySetResult(true);
                                    await releaseSecondSibling.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                });
                            });
                    });
            });
        });

        /* Start the real scheduler, wait until the lock holder definitely owns the shared lock, then wait until the
           first sibling has inserted its child-operation root. At that point the second sibling is independently ready,
           so leaving it pending demonstrates the scheduler bug. */
        (ExecutionPlan _, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime((Operation)operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        try
        {
            await lockHolderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await RuntimeTestUtilities.WaitForConditionAsync(
                () => insertedChildRootTaskId != default && session.Tasks.Any(task => task.Id == insertedChildRootTaskId),
                TimeSpan.FromSeconds(5),
                "Timed out waiting for the expected runtime task shape.");

            ExecutionTask secondSiblingTask = session.GetTask(secondSiblingVisibleTaskId);
            Assert.Equal(ExecutionTaskState.Running, secondSiblingTask.State);
        }
        finally
        {
            /* Always release both held branches so a failing assertion does not strand the scheduler run. */
            releaseLockHolder.TrySetResult(true);
            releaseSecondSibling.TrySetResult(true);
        }

        /* The run should still finish successfully once cleanup releases the held work. */
        OperationResult result = await executeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ExecutionTaskOutcome.Completed, result.Outcome);
        Assert.True(secondSiblingStarted.Task.IsCompleted, "The independently ready sibling never started during execution.");
    }
}
