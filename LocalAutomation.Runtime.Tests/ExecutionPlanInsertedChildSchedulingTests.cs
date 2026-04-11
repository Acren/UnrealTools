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
    /// Confirms that live child-operation insertion should not break concurrent traversal of the live session graph.
    /// </summary>
    [Fact]
    public async Task LiveChildInsertionShouldNotBreakConcurrentGraphTraversal()
    {
        /* Drive one real RunChildOperationAsync merge while a separate test-controlled reader repeatedly traverses the
           live session graph. This creates an intentional read/write overlap window without relying on a long stress
           loop whose runtime dominates the actual assertion. */
        TaskCompletionSource<bool> inserterStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> allowInsertion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> traversalStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> traversalFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource stopTraversal = new();
        ExecutionTaskId traversalTargetTaskId = default;

        var operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Insert Child Operation").Run(async context =>
                {
                    /* Wait until the test harness has started the concurrent graph reader so the merge happens inside a known
                       overlap window rather than at some arbitrary point during scheduler startup. */
                    inserterStarted.TrySetResult(true);
                    await allowInsertion.Task.WaitAsync(TimeSpan.FromSeconds(5));

                    var childOperation = new ExecutionTestCommon.InlineOperation(
                        childRoot =>
                        {
                            /* Insert a small authored subtree in one merge so the writer side stays active long enough for
                               the deterministic concurrent reader to overlap the same live child list without falling
                               back to hundreds of separate child-operation calls. */
                            childRoot.Children(insertedScope =>
                            {
                                for (int index = 0; index < 24; index += 1)
                                {
                                    insertedScope.Task($"Inserted Traversal Task {index}")
                                        .Run(async _ =>
                                        {
                                            await Task.Yield();
                                        });
                                }
                            });
                        },
                        operationName: "Inserted Child Operation");

                    OperationParameters childParameters = childOperation.CreateParameters(context.ValidatedOperationParameters.CreateChild());
                    OperationResult childResult = await context.RunChildOperationAsync((Operation)childOperation, childParameters);
                    if (!childResult.Success)
                    {
                        throw new InvalidOperationException(childResult.FailureReason ?? "Inserted child operation failed.");
                    }
                });

                /* Put the traversal target in a second root branch. A concurrent GetTask/GetTaskDisplayPath walk to this
                   target must traverse the inserter branch first, so the reader and writer contend over the same live
                   child list without needing a long stress loop. */
                scope.Task("Traversal Branch").Children(traversalScope =>
                {
                    for (int index = 0; index < 16; index += 1)
                    {
                        traversalScope.Task($"Traversal Target {index}", out traversalTargetTaskId)
                            .When(false, "Disabled traversal-only task.")
                            .Run(() => Task.CompletedTask);
                    }
                });
            });
        });

        (ExecutionPlan _, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime((Operation)operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        Task traversalTask = Task.Run(() =>
        {
            /* Keep traversing the live graph until the operation finishes so the old unfixed runtime reliably performs
               concurrent reads while the task body is merging its child operation into the same session tree. */
            traversalStarted.TrySetResult(true);
            try
            {
                while (!stopTraversal.Token.IsCancellationRequested)
                {
                    _ = session.GetTaskDisplayPath(traversalTargetTaskId);
                }
            }
            catch (Exception ex)
            {
                traversalFailed.TrySetException(ex);
            }
        }, stopTraversal.Token);

        try
        {
            /* Start the merge only after the concurrent traversal loop is already hot, so one child-operation insertion is
               enough to probe the original race instead of relying on dozens of stress iterations. */
            await inserterStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await traversalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            allowInsertion.TrySetResult(true);

            /* Keep the explicit overlap window short and deliberate. The deterministic reader only needs to be active
               while the child merge is in flight; after that, stop the reader so the test does not conflate the race
               itself with long-running concurrent traversal noise. */
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            stopTraversal.Cancel();
            try
            {
                await traversalTask;
            }
            catch (OperationCanceledException)
            {
            }

            /* This should complete cleanly. Before the traversal fix, the bounded overlap window can still fault with
               collection-modified or related live-graph traversal exceptions. */
            if (traversalFailed.Task.IsCompleted)
            {
                await traversalFailed.Task;
            }

            OperationResult result = await executeTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ExecutionTaskOutcome.Completed, result.Outcome);
        }
        finally
        {
            stopTraversal.Cancel();
            try
            {
                await traversalTask;
            }
            catch (OperationCanceledException)
            {
            }
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
                                lockHolderRoot.WithExecutionLocks(sharedLock).Run(async _ =>
                                {
                                    lockHolderStarted.TrySetResult(true);
                                    await releaseLockHolder.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                });
                            },
                            operationName: "Lock Holder Child Operation");

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
                                                childRoot.WithExecutionLocks(sharedLock).Run(() => Task.CompletedTask);
                                            },
                                            operationName: "Inserted Child Operation");

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
