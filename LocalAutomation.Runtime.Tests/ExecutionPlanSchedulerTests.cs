using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TestUtilities;
using Xunit;

namespace LocalAutomation.Runtime.Tests;

public sealed class ExecutionPlanSchedulerTests
{
    /// <summary>
    /// Confirms that sibling failure should interrupt already-running collateral work rather than labeling it as a user
    /// cancellation.
    /// </summary>
    [Fact]
    public async Task SiblingFailureInterruptsCollateralWork()
    {
        /* Branch A waits until the test explicitly releases it after both active tasks are confirmed running. That keeps
           the scenario focused on interrupting already-running collateral work instead of startup-time skipping. */
        TaskCompletionSource<bool> allowBranchAFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId branchAActiveTaskId = default;
        ExecutionTaskId branchBActiveTaskId = default;
        ExecutionTaskId branchBQueuedBodyTaskId = default;

        /* The plan shape mirrors the production scenario in the smallest possible form:
           - Branch A has one active body task that will fail and one queued body task that never matters.
           - Branch B has one active body task that only stops when cancellation reaches it and one queued body task that
             should stay untouched.
           When Branch A fails, Branch B.Active Work is already running and Branch B.Queued Work is still pending. */
        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Branch A").Children(branchAScope =>
                {
                    /* Capture the execution task id directly from Run(..., out ...) so the test can await the live runtime
                       task by identity without rediscovering the hidden body node later. */
                    branchAScope.Task("Active Work").Run(async _ =>
                    {
                        await allowBranchAFailure.Task.WaitAsync(TimeSpan.FromSeconds(1));
                        return OperationResult.Failed(failureReason: "Branch A failed.");
                    }, out branchAActiveTaskId);
                    branchAScope.Task("Queued Work").Run(() => Task.FromResult(OperationResult.Succeeded()));
                });

                scope.Task("Branch B").Children(branchBScope =>
                {
                    /* This body task blocks forever until the scheduler cancels it. That makes it the collateral running
                       work whose semantic outcome should become Interrupted, not Cancelled, when Branch A fails. */
                    branchBScope.Task("Active Work").Run(RuntimeTestUtilities.RunUntilCancelled, out branchBActiveTaskId);
                    branchBScope.Task("Queued Work").Run(context =>
                    {
                        return Task.FromResult(OperationResult.Succeeded());
                    }, out branchBQueuedBodyTaskId);
                });
            });
        });

        /* This scenario needs direct scheduler control so the test can wait for both active tasks to reach Running before
           allowing Branch A to fail. */
        (ExecutionPlan plan, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        await session.GetTask(branchAActiveTaskId).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await session.GetTask(branchBActiveTaskId).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));
        allowBranchAFailure.TrySetResult(true);

        OperationResult result = await executeTask;

        Assert.NotEqual(default, branchAActiveTaskId);
        Assert.NotEqual(default, branchBActiveTaskId);
        Assert.NotEqual(default, branchBQueuedBodyTaskId);

        /* Expected outcomes:
           - overall run fails because Branch A failed directly,
           - Branch A.Active Work is Failed,
           - Branch B.Active Work is Interrupted because sibling failure stopped it,
           - Branch B.Queued Work is Skipped because it never started. */
        Assert.Equal(ExecutionTaskOutcome.Failed, result.Outcome);
        Assert.Equal(ExecutionTaskOutcome.Failed, session.GetTask(branchAActiveTaskId).Outcome);
        Assert.Equal(ExecutionTaskOutcome.Interrupted, session.GetTask(branchBActiveTaskId).Outcome);
        Assert.Equal(ExecutionTaskOutcome.Skipped, session.GetTask(branchBQueuedBodyTaskId).Outcome);
    }

    /// <summary>
    /// Confirms that explicit user cancellation still produces Cancelled for active work while queued work remains Skipped.
    /// </summary>
    [Fact]
    public async Task ExplicitUserCancellationKeepsActiveWorkCancelled()
    {
        /* Both active branches block until the test cancels the scheduler token, which isolates the user-cancel path from
           the sibling-failure interruption path above. */
        ExecutionTaskId branchAActiveTaskId = default;
        ExecutionTaskId branchBActiveTaskId = default;
        ExecutionTaskId branchAQueuedBodyTaskId = default;

        /* This scenario uses the same two-branch shape, but neither branch fails on its own.
           The only stop signal comes from the explicit cancellation token passed to ExecuteAsync. That means active work
           should stay Cancelled rather than Interrupted. */
        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Branch A").Children(branchAScope =>
                {
                    /* Capture the active execution task id so the test can await the real runtime task reaching Running
                       before it cancels the session. */
                    branchAScope.Task("Active Work").Run(RuntimeTestUtilities.RunUntilCancelled, out branchAActiveTaskId);
                    branchAScope.Task("Queued Work").Run(context =>
                    {
                        return Task.FromResult(OperationResult.Succeeded());
                    }, out branchAQueuedBodyTaskId);
                });

                scope.Task("Branch B").Children(branchBScope =>
                {
                    branchBScope.Task("Active Work").Run(RuntimeTestUtilities.RunUntilCancelled, out branchBActiveTaskId);
                });
            });
        });

        /* This case still needs the authored plan before execution only because the scheduler is started manually so the
           test can cancel it at the right time. The queued body-task id is captured directly from Run(..., out id), so
           no string-based plan lookup is needed. */
        (ExecutionPlan plan, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        using CancellationTokenSource cancellationSource = new();

        /* Wait until both active branches are definitely running before cancelling so the assertions exercise active-work
           cancellation semantics instead of startup-time skipping. */
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(cancellationSource.Token);
        await session.GetTask(branchAActiveTaskId).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await session.GetTask(branchBActiveTaskId).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));
        cancellationSource.Cancel();

        OperationResult result = await executeTask;

        Assert.NotEqual(default, branchAActiveTaskId);
        Assert.NotEqual(default, branchBActiveTaskId);
        Assert.NotEqual(default, branchAQueuedBodyTaskId);

        /* Expected outcomes:
           - overall run is Cancelled because the user token ended the run,
           - both active body tasks are Cancelled,
           - the untouched queued body task is Skipped because it never started. */
        Assert.Equal(ExecutionTaskOutcome.Cancelled, result.Outcome);
        Assert.Equal(ExecutionTaskOutcome.Cancelled, session.GetTask(branchAActiveTaskId).Outcome);
        Assert.Equal(ExecutionTaskOutcome.Cancelled, session.GetTask(branchBActiveTaskId).Outcome);
        Assert.Equal(ExecutionTaskOutcome.Skipped, session.GetTask(branchAQueuedBodyTaskId).Outcome);
    }

    /// <summary>
    /// Confirms that a body task blocked only on an execution lock should remain pending until the scheduler can actually
    /// start executing its body.
    /// </summary>
    [Fact]
    public async Task TaskWaitingForExecutionLockStaysPending()
    {
        // Arrange: two parallel body tasks contend for the same lock, and branch A holds it open.
        ExecutionLock sharedLock = new("pending-lock");
        TaskCompletionSource<bool> branchAStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseBranchA = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId branchATaskId = default;
        ExecutionTaskId branchBTaskId = default;

        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Branch A").Run(RuntimeTestUtilities.RunUntilReleased(branchAStarted, releaseBranchA), out branchATaskId);
                scope.Task("Branch B").Run(_ => Task.FromResult(OperationResult.Succeeded()), out branchBTaskId);
            });
        }, sharedLock);

        // Act: start the scheduler and wait until the lock holder is definitely running.
        (ExecutionPlan plan, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        await branchAStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await session.GetTask(branchATaskId).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));

        // Assert: the contended task should still be pending because it has not acquired the lock yet.
        ExecutionTask blockedTask = session.GetTask(branchBTaskId);
        Assert.Equal(ExecutionTaskState.Pending, blockedTask.State);

        // Cleanup: release the lock holder so the run can finish normally.
        releaseBranchA.TrySetResult(true);
        OperationResult result = await executeTask;

        // Assert: once the lock is released, the blocked task should still be able to complete successfully.
        Assert.Equal(ExecutionTaskOutcome.Completed, result.Outcome);
        Assert.Equal(ExecutionTaskOutcome.Completed, session.GetTask(branchBTaskId).Outcome);
    }

    /// <summary>
    /// Confirms that equally ready sibling body tasks start in declared order when a shared lock forces the scheduler to
    /// choose exactly one winner first.
    /// </summary>
    [Fact]
    public async Task ReadySiblingBodiesStartInDeclaredOrder()
    {
        // Arrange: first and second are equally ready, but a shared lock means only one body can actually start first.
        ExecutionLock sharedLock = new("declared-order-lock");
        TaskCompletionSource<ExecutionTaskId> firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseWinner = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId firstTaskId = default;
        ExecutionTaskId secondTaskId = default;

        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("First").Run(async context =>
                {
                    firstStarted.TrySetResult(context.TaskId);
                    await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return OperationResult.Succeeded();
                }, out firstTaskId);
                scope.Task("Second").Run(async context =>
                {
                    firstStarted.TrySetResult(context.TaskId);
                    await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return OperationResult.Succeeded();
                }, out secondTaskId);
            });
        }, sharedLock);

        // Act: execute once and capture whichever sibling body actually started first.
        (ExecutionPlan plan, _, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        ExecutionTaskId winnerTaskId = await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        releaseWinner.TrySetResult(true);
        OperationResult result = await executeTask;

        // Assert: declared-first work should win the shared lock and the overall run still completes successfully.
        Assert.NotEqual(default, firstTaskId);
        Assert.NotEqual(default, secondTaskId);
        Assert.Equal(firstTaskId, winnerTaskId);
        Assert.Equal(ExecutionTaskOutcome.Completed, result.Outcome);
    }

    /// <summary>
    /// Confirms that the same declared-first sibling keeps winning across fresh plan builds, which would fail if the
    /// scheduler were still tie-breaking equally ready work by random task id.
    /// </summary>
    [Fact]
    public async Task ReadySiblingBodiesStayDeterministicAcrossFreshPlans()
    {
        // Arrange: each run rebuilds the same parallel sibling shape so fresh task ids cannot affect the winner.
        for (int runIndex = 0; runIndex < 10; runIndex += 1)
        {
            ExecutionLock sharedLock = new($"determinism-lock-{runIndex}");
            TaskCompletionSource<ExecutionTaskId> winnerSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> releaseWinner = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ExecutionTaskId firstTaskId = default;
            ExecutionTaskId secondTaskId = default;

            Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
            {
                root.Children(ExecutionChildMode.Parallel, scope =>
                {
                    scope.Task("First").Run(async context =>
                    {
                        winnerSource.TrySetResult(context.TaskId);
                        await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        return OperationResult.Succeeded();
                    }, out firstTaskId);
                    scope.Task("Second").Run(async context =>
                    {
                        winnerSource.TrySetResult(context.TaskId);
                        await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        return OperationResult.Succeeded();
                    }, out secondTaskId);
                });
            }, sharedLock);

            // Act: rebuild and run the same authored shape repeatedly, recording the first-starting sibling each time.
            (ExecutionPlan plan, _, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
            Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
            ExecutionTaskId winnerTaskId = await winnerSource.Task.WaitAsync(TimeSpan.FromSeconds(1));
            releaseWinner.TrySetResult(true);
            OperationResult result = await executeTask;

            // Assert: every fresh plan build should still prefer the declared-first sibling.
            Assert.NotEqual(default, firstTaskId);
            Assert.NotEqual(default, secondTaskId);
            Assert.Equal(firstTaskId, winnerTaskId);
            Assert.Equal(ExecutionTaskOutcome.Completed, result.Outcome);
        }
    }

    /// <summary>
    /// Confirms that when multiple ready tasks are blocked on the same execution lock, the scheduler should prefer the
    /// task that unlocks more downstream dependent work after it completes.
    /// </summary>
    [Fact]
    public async Task LockReleasePrefersTaskWithMoreDownstreamDependents()
    {
        // Arrange: a lock holder runs first, then two equally ready contenders wait on the same lock. The declared-first
        // contender has no downstream dependents, while the declared-second contender unlocks one extra dependent task.
        // The expected policy should therefore choose the declared-second contender once the lock becomes available.
        ExecutionLock sharedLock = new("dependent-priority-lock");
        TaskCompletionSource<bool> lockHolderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseLockHolder = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ExecutionTaskId> contenderWinner = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseWinner = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId branchATaskId = default;
        ExecutionTaskId branchBTaskId = default;

        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Lock Holder").Run(RuntimeTestUtilities.RunUntilReleased(lockHolderStarted, releaseLockHolder));

                scope.Task("Sequence")
                    .Children(sequence =>
                    {
                        sequence.Task("Prepare")
                            .Run(() => Task.CompletedTask);

                        sequence.Task("Parallel")
                            .Children(ExecutionChildMode.Parallel, parallel =>
                            {
                                parallel.Task("Branch A")
                                    .Run(async context =>
                                    {
                                        contenderWinner.TrySetResult(context.TaskId);
                                        await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                        return OperationResult.Succeeded();
                                    }, out branchATaskId);

                                parallel.Task("Branch B")
                                    .Run(async context =>
                                    {
                                        contenderWinner.TrySetResult(context.TaskId);
                                        await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                        return OperationResult.Succeeded();
                                    }, out branchBTaskId)
                                    .Then("Branch B Dependent")
                                    .Run(() => Task.CompletedTask);
                            });
                    });
            });
        }, sharedLock);

        // Act: wait until the lock holder definitely owns the shared lock, then release it and capture which waiting
        // contender starts first once the shared lock becomes available.
        (ExecutionPlan _, _, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        await lockHolderStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        releaseLockHolder.TrySetResult(true);
        ExecutionTaskId winnerTaskId = await contenderWinner.Task.WaitAsync(TimeSpan.FromSeconds(1));
        releaseWinner.TrySetResult(true);
        OperationResult result = await executeTask;

        // Assert: the contender with more downstream dependent work should win the lock once it is released.
        Assert.NotEqual(default, branchATaskId);
        Assert.NotEqual(default, branchBTaskId);
        Assert.Equal(branchBTaskId, winnerTaskId);
        Assert.Equal(ExecutionTaskOutcome.Completed, result.Outcome);
    }

    /// <summary>
    /// Confirms that lock priority still follows the outer runtime branch's downstream chain when each contender is
    /// currently blocked inside an inserted child operation.
    /// </summary>
    [Fact]
    public async Task LockReleasePrefersOuterBranchWithMoreDownstreamDependentsAcrossInsertedChildOperations()
    {
        /* Hold the shared lock with a separate child operation first so both branch bodies can insert their own child
           operations and block on the same lock before the scheduler has to choose one winner. */
        ExecutionLock sharedLock = new("inserted-child-dependent-priority-lock");
        TaskCompletionSource<bool> lockHolderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseLockHolder = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ExecutionTaskId> contenderWinner = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseWinner = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId shortChildRootTaskId = default;
        ExecutionTaskId shortChildBodyTaskId = default;
        ExecutionTaskId longChildRootTaskId = default;
        ExecutionTaskId longChildBodyTaskId = default;

        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Lock Holder")
                    .Run(async context =>
                    {
                        /* The lock-holder child operation owns the shared lock while the real contenders expand beneath
                           their outer branch tasks. */
                        Operation lockHolderOperation = new ExecutionTestCommon.InlineOperation(
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
                        OperationResult lockHolderResult = await context.RunChildOperationAsync(lockHolderOperation, lockHolderParameters);
                        if (!lockHolderResult.Success)
                        {
                            throw new InvalidOperationException(lockHolderResult.FailureReason ?? "Lock holder child operation failed.");
                        }
                    });

                scope.Task("Parallel Work")
                    .Children(ExecutionChildMode.Parallel, parallel =>
                    {
                        parallel.Task("Short Branch")
                            .Run(async context =>
                            {
                                /* This branch has no outer follow-up work after its inserted child operation completes. */
                                Operation shortChildOperation = new ExecutionTestCommon.InlineOperation(
                                    shortChildRoot =>
                                    {
                                        shortChildRootTaskId = shortChildRoot.Id;
                                        shortChildRoot.Run(async childContext =>
                                        {
                                            contenderWinner.TrySetResult(childContext.TaskId);
                                            await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                        }, out shortChildBodyTaskId);
                                    },
                                    operationName: "Short Child Operation",
                                    executionLocks: new[] { sharedLock });

                                OperationParameters shortChildParameters = shortChildOperation.CreateParameters(context.ValidatedOperationParameters.CreateChild());
                                OperationResult shortChildResult = await context.RunChildOperationAsync(shortChildOperation, shortChildParameters);
                                if (!shortChildResult.Success)
                                {
                                    throw new InvalidOperationException(shortChildResult.FailureReason ?? "Short child operation failed.");
                                }
                            });

                        parallel.Task("Long Branch")
                            .Run(async context =>
                            {
                                /* This branch should win because its visible outer task unlocks additional runtime work
                                   after the inserted child operation completes. */
                                Operation longChildOperation = new ExecutionTestCommon.InlineOperation(
                                    longChildRoot =>
                                    {
                                        longChildRootTaskId = longChildRoot.Id;
                                        longChildRoot.Run(async childContext =>
                                        {
                                            contenderWinner.TrySetResult(childContext.TaskId);
                                            await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                        }, out longChildBodyTaskId);
                                    },
                                    operationName: "Long Child Operation",
                                    executionLocks: new[] { sharedLock });

                                OperationParameters longChildParameters = longChildOperation.CreateParameters(context.ValidatedOperationParameters.CreateChild());
                                OperationResult longChildResult = await context.RunChildOperationAsync(longChildOperation, longChildParameters);
                                if (!longChildResult.Success)
                                {
                                    throw new InvalidOperationException(longChildResult.FailureReason ?? "Long child operation failed.");
                                }
                            })
                            .Then("Long Branch Follow-up")
                            .Run(() => Task.CompletedTask);
                    });
            });
        });

        /* Wait until both inserted child-operation roots exist while the separate lock holder still owns the shared lock.
           That isolates the arbitration point to the scheduler's next winner choice after release. */
        (ExecutionPlan _, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        await lockHolderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline
            && (!session.Tasks.Any(task => task.Id == shortChildRootTaskId)
                || !session.Tasks.Any(task => task.Id == longChildRootTaskId)))
        {
            await Task.Delay(10);
        }

        Assert.NotEqual(default, shortChildRootTaskId);
        Assert.NotEqual(default, shortChildBodyTaskId);
        Assert.NotEqual(default, longChildRootTaskId);
        Assert.NotEqual(default, longChildBodyTaskId);
        Assert.True(session.Tasks.Any(task => task.Id == shortChildRootTaskId), "Short branch never inserted its child operation.");
        Assert.True(session.Tasks.Any(task => task.Id == longChildRootTaskId), "Long branch never inserted its child operation.");

        releaseLockHolder.TrySetResult(true);
        ExecutionTaskId winnerTaskId = await contenderWinner.Task.WaitAsync(TimeSpan.FromSeconds(1));
        releaseWinner.TrySetResult(true);
        OperationResult result = await executeTask;

        /* The outer long branch should win even though the concrete lock contender is the inserted child operation body. */
        Assert.Equal(longChildBodyTaskId, winnerTaskId);
        Assert.Equal(ExecutionTaskOutcome.Completed, result.Outcome);
    }

    /// <summary>
    /// Confirms that interleaved body and child declarations execute in the same explicit order they were authored on the
    /// parent task.
    /// </summary>
    [Fact]
    public async Task InterleavedBodiesAndChildrenKeepExplicitOrder()
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
            root.Run(() =>
            {
                Record("root-body-1");
                return Task.CompletedTask;
            });

            root.Child("Child").Run(() =>
            {
                Record("child-body");
                return Task.CompletedTask;
            });

            root.Run(() =>
            {
                Record("root-body-2");
                return Task.CompletedTask;
            });

            root.Children(scope =>
            {
                scope.Task("Nested").Run(() =>
                {
                    Record("nested-body");
                    return Task.CompletedTask;
                });
            });
        });

        // Act: execute the authored plan through the real runtime pipeline.
        (_, _, OperationResult result) = await RuntimeTestUtilities.ExecuteAsync(operation);

        // Assert: the bodies should run in the same interleaved order that the plan author declared them.
        Assert.Equal(ExecutionTaskOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "root-body-1", "child-body", "root-body-2", "nested-body" }, executionOrder);
    }

    /// <summary>
    /// Confirms that a container scope with no running descendants stays pending while its child is still blocked on an
    /// unsatisfied dependency.
    /// </summary>
    [Fact]
    public async Task ParentScopeStaysPendingWhileChildWaitsForDependencies()
    {
        // Arrange: the branch child depends on a separate body task that is still running.
        TaskCompletionSource<bool> releaseDependency = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId dependencyTaskId = default;
        ExecutionTaskId waitingChildTaskId = default;

        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(scope =>
            {
                scope.Task("Dependency").Run(RuntimeTestUtilities.RunUntilReleased(new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously), releaseDependency), out dependencyTaskId);
                scope.Task("Branch").Children(branchScope =>
                {
                    branchScope.Task("Waiting Child").After(dependencyTaskId).Run(_ => Task.FromResult(OperationResult.Succeeded()), out waitingChildTaskId);
                });
            });
        });

        // Act: start the scheduler and wait until the dependency body task has definitely started.
        (ExecutionPlan plan, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        await session.GetTask(dependencyTaskId).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));

        // Assert: both the waiting child and its parent scope should still read as pending.
        ExecutionTask waitingChild = session.GetTask(waitingChildTaskId);
        ExecutionTask parentScope = session.GetTask(waitingChild.ParentId!.Value);
        Assert.Equal(ExecutionTaskState.Pending, waitingChild.State);
        Assert.Equal(ExecutionTaskState.Pending, parentScope.State);

        // Cleanup: release the dependency so the run can complete.
        releaseDependency.TrySetResult(true);
        await executeTask;
    }

    /// <summary>
    /// Confirms that a container scope with a child blocked only on a contended execution lock stays pending until one of
    /// its descendants actually starts running.
    /// </summary>
    [Fact]
    public async Task ParentScopeStaysPendingWhileChildWaitsForExecutionLock()
    {
        // Arrange: each branch contains one active child, but branch A holds the shared lock open first.
        ExecutionLock sharedLock = new("parent-pending-lock");
        TaskCompletionSource<bool> branchAStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseBranchA = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> branchBStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseBranchB = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId branchABodyTaskId = default;
        ExecutionTaskId branchBBodyTaskId = default;

        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Branch A").Children(branchScope =>
                {
                    branchScope.Task("Active Work").Run(RuntimeTestUtilities.RunUntilReleased(branchAStarted, releaseBranchA), out branchABodyTaskId);
                });

                scope.Task("Branch B").Children(branchScope =>
                {
                    branchScope.Task("Active Work").Run(RuntimeTestUtilities.RunUntilReleased(branchBStarted, releaseBranchB), out branchBBodyTaskId);
                });
            });
        }, sharedLock);

        // Act: start the scheduler and wait until branch A is definitely running with the shared lock.
        (ExecutionPlan plan, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        await branchAStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await session.GetTask(branchABodyTaskId).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));

        // Assert: branch B should still be pending because its child cannot start until the lock becomes available.
        ExecutionTask branchBTask = session.GetTask(branchBBodyTaskId);
        ExecutionTask branchBScope = session.GetTask(branchBTask.ParentId!.Value);
        Assert.Equal(ExecutionTaskState.Pending, branchBTask.State);
        Assert.Equal(ExecutionTaskState.Pending, branchBScope.State);

        // Cleanup: release both branches in sequence so the run can complete if the scheduler eventually starts branch B.
        releaseBranchA.TrySetResult(true);
        await branchBStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        releaseBranchB.TrySetResult(true);
        await executeTask;
    }
}
