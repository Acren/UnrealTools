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
        ExecutionTaskId branchBVisibleTaskId = default;
        ExecutionTaskId branchATaskId = default;
        ExecutionTaskId branchBTaskId = default;

        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Branch A").Run(RuntimeTestUtilities.RunUntilReleased(branchAStarted, releaseBranchA), out branchATaskId);
                scope.Task("Branch B", out branchBVisibleTaskId).Run(_ => Task.FromResult(OperationResult.Succeeded()), out branchBTaskId);
            });
        }, sharedLock);

        // Act: start the scheduler and wait until the lock holder is definitely running.
        (ExecutionPlan plan, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        await branchAStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await session.GetTask(branchATaskId).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));

        // Assert: the visible/authored task that declared the lock should surface the explicit lock-wait state rather
        // than leaving that state hidden on the executable body task below it.
        Assert.NotEqual(default, branchBVisibleTaskId);
        ExecutionTask blockedTask = session.GetTask(branchBVisibleTaskId);
        Assert.Equal(ExecutionTaskState.WaitingForExecutionLock, blockedTask.State);

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
    /// task that unlocks more downstream work after it completes.
    /// </summary>
    [Fact]
    public async Task LockReleasePrefersTaskWithMoreDownstreamWork()
    {
        // Arrange: hold the shared lock inside one running branch first, then insert the real contenders only after that
        // lock holder is definitely active so lock release is observed through ordinary scheduler completion flow.
        ExecutionLock sharedLock = new("dependent-priority-lock");
        TaskCompletionSource<bool> lockHolderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseLockHolder = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ExecutionTaskId> contenderWinner = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseWinner = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId branchAChildBodyTaskId = default;
        ExecutionTaskId branchBChildBodyTaskId = default;

        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Lock Holder")
                    .Run(async context =>
                    {
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

                scope.Task("Expand Contenders")
                    .Run(async context =>
                    {
                        await lockHolderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                        /* Insert both same-lock contenders only after the lock holder already owns the shared lock. */
                        Operation contendersOperation = new ExecutionTestCommon.InlineOperation(
                            contenderRoot =>
                            {
                                contenderRoot.Children(ExecutionChildMode.Parallel, parallel =>
                                {
                                    parallel.Task("Branch A")
                                        .Run(async childContext =>
                                        {
                                            contenderWinner.TrySetResult(childContext.TaskId);
                                            await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                        }, out branchAChildBodyTaskId);

                                    parallel.Task("Branch B")
                                        .Run(async childContext =>
                                        {
                                            contenderWinner.TrySetResult(childContext.TaskId);
                                            await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                        }, out branchBChildBodyTaskId)
                                        .Then("Branch B Dependent")
                                        .Run(() => Task.CompletedTask);
                                });
                            },
                            operationName: "Contenders Child Operation",
                            executionLocks: new[] { sharedLock });

                        OperationParameters contendersParameters = contendersOperation.CreateParameters(context.ValidatedOperationParameters.CreateChild());
                        OperationResult contendersResult = await context.RunChildOperationAsync(contendersOperation, contendersParameters);
                        if (!contendersResult.Success)
                        {
                            throw new InvalidOperationException(contendersResult.FailureReason ?? "Contenders child operation failed.");
                        }
                    });
            });
        });

        // Act: wait until the lock holder definitely owns the shared lock and both contender bodies have been inserted,
        // then release the lock-holder branch and capture which waiting contender starts first.
        (ExecutionPlan _, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        ExecutionTaskId winnerTaskId;
        try
        {
            await lockHolderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await RuntimeTestUtilities.WaitForConditionAsync(
                () => branchAChildBodyTaskId != default
                    && branchBChildBodyTaskId != default
                    && session.Tasks.Any(task => task.Id == branchAChildBodyTaskId)
                    && session.Tasks.Any(task => task.Id == branchBChildBodyTaskId),
                TimeSpan.FromSeconds(5),
                "Timed out waiting for both lock contenders to reach the shared-lock boundary.");

            releaseLockHolder.TrySetResult(true);
            winnerTaskId = await contenderWinner.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            releaseLockHolder.TrySetResult(true);
            releaseWinner.TrySetResult(true);
        }

        OperationResult result = await executeTask;

        // Assert: the contender with more downstream work should win the lock once it is released.
        Assert.NotEqual(default, branchAChildBodyTaskId);
        Assert.NotEqual(default, branchBChildBodyTaskId);
        Assert.Equal(branchBChildBodyTaskId, winnerTaskId);
        Assert.Equal(ExecutionTaskOutcome.Completed, result.Outcome);
    }

    /// <summary>
    /// Confirms that when multiple runtime-parallel tasks become ready together, the scheduler should start the task whose
    /// completion unlocks more downstream work before shorter sibling branches.
    /// </summary>
    [Fact]
    public async Task ReadyParallelTasksPreferMostDownstreamWorkFirst()
    {
        /* Keep the winning body task open so the first Running transition captured from the live session reflects the
           scheduler's actual start choice instead of a race between very short async bodies. */
        TaskCompletionSource<bool> releaseWinner = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId shortBranchTaskId = default;
        ExecutionTaskId longBranchTaskId = default;
        ExecutionTaskId firstStartedTaskId = default;

        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Short Branch")
                    .Run(async _ =>
                    {
                        await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        return OperationResult.Succeeded();
                    }, out shortBranchTaskId);

                scope.Task("Long Branch")
                    .Run(async _ =>
                    {
                        await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        return OperationResult.Succeeded();
                    }, out longBranchTaskId)
                    .Then("Long Branch Follow-up 1")
                    .Run(() => Task.CompletedTask)
                    .Then("Long Branch Follow-up 2")
                    .Run(() => Task.CompletedTask);
            });
        });

        /* Capture the first body task that transitions to Running among the two parallel contenders. The scheduler sets
           Running synchronously when it chooses a task, so this observes start order directly without relying on titles. */
        (ExecutionPlan _, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        session.TaskStateChanged += OnTaskStateChanged;
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        try
        {
            await RuntimeTestUtilities.WaitForConditionAsync(() => firstStartedTaskId != default, TimeSpan.FromSeconds(1));
        }
        finally
        {
            session.TaskStateChanged -= OnTaskStateChanged;
            releaseWinner.TrySetResult(true);
        }

        OperationResult result = await executeTask;

        Assert.NotEqual(default, shortBranchTaskId);
        Assert.NotEqual(default, longBranchTaskId);
        Assert.Equal(longBranchTaskId, firstStartedTaskId);
        Assert.Equal(ExecutionTaskOutcome.Completed, result.Outcome);

        void OnTaskStateChanged(ExecutionTaskId taskId, ExecutionTaskState state, ExecutionTaskOutcome? _, string? __)
        {
            if (state != ExecutionTaskState.Running || firstStartedTaskId != default)
            {
                return;
            }

            if (taskId == shortBranchTaskId || taskId == longBranchTaskId)
            {
                firstStartedTaskId = taskId;
            }
        }
    }

    /// <summary>
    /// Confirms that lock priority still follows the outer runtime branch's downstream work when each contender is
    /// currently blocked inside an inserted child operation.
    /// </summary>
    [Fact]
    public async Task LockReleasePrefersOuterBranchWithMoreDownstreamWorkAcrossInsertedChildOperations()
    {
        /* Hold the shared lock inside one running child operation first so both branch bodies can insert their own child
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
                                await lockHolderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

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
                                await lockHolderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

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

        /* Wait until the lock holder definitely owns the shared lock and both inserted child-operation roots exist.
           That isolates the arbitration point to the scheduler's next winner choice after release. */
        (ExecutionPlan _, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        ExecutionTaskId winnerTaskId;
        try
        {
            await lockHolderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await RuntimeTestUtilities.WaitForConditionAsync(
                () => shortChildRootTaskId != default
                    && longChildRootTaskId != default
                    && session.Tasks.Any(task => task.Id == shortChildRootTaskId)
                    && session.Tasks.Any(task => task.Id == longChildRootTaskId),
                TimeSpan.FromSeconds(5),
                "Timed out waiting for both inserted child operations to reach the shared-lock boundary.");

            Assert.NotEqual(default, shortChildBodyTaskId);
            Assert.NotEqual(default, longChildBodyTaskId);

            releaseLockHolder.TrySetResult(true);
            winnerTaskId = await contenderWinner.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            releaseLockHolder.TrySetResult(true);
            releaseWinner.TrySetResult(true);
        }

        OperationResult result = await executeTask;

        /* The outer long branch should win even though the concrete lock contender is the inserted child operation body. */
        Assert.Equal(longChildBodyTaskId, winnerTaskId);
        Assert.Equal(ExecutionTaskOutcome.Completed, result.Outcome);
    }

    /// <summary>
    /// Confirms that releasing a shared execution lock wakes the waiting lock consumer promptly even when a higher-priority
    /// lock-free follow-up task begins long synchronous setup work in the same scheduler pass.
    /// </summary>
    [Fact]
    public async Task WaitingTaskStartsPromptlyAfterExecutionLockRelease()
    {
        /* Keep the shared lock on the outer operation so the direct lock holder and waiting task contend for the same
           lock, but move the higher-priority follow-up work into a separate lock-free child operation instance. That
           makes lock release and follow-up readiness happen on the same completed task without a parent waiting on its own
           same-lock child operation. */
        ExecutionLock sharedLock = new("lock-release-wakeup-lock");
        TaskCompletionSource<bool> lockHolderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseLockHolder = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseFollowUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ExecutionTaskId> waitingTaskStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> followUpStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseWaitingTask = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId waitingVisibleTaskId = default;
        ExecutionTaskId waitingTaskId = default;
        ExecutionTaskId lockHolderTaskId = default;
        Operation blockingFollowUpOperation = new ExecutionTestCommon.InlineOperation(
            root =>
            {
                /* Child operations still need one explicit child task beneath their root before authored sequential
                   siblings can be declared. The blocking startup task is the actual scheduler contender; the dependents
                   only increase its downstream-work priority once the lock holder finishes. */
                root.Children(childScope =>
                {
                    childScope.Task("Blocking Startup")
                        .Run(_ =>
                        {
                            followUpStarted.TrySetResult(true);
                            releaseFollowUp.Task.Wait(TimeSpan.FromSeconds(5));
                            return Task.FromResult(OperationResult.Succeeded());
                        })
                        .Then("Long Lock-Free Follow-up Dependent 1")
                        .Run(() => Task.CompletedTask)
                        .Then("Long Lock-Free Follow-up Dependent 2")
                        .Run(() => Task.CompletedTask)
                        .Then("Long Lock-Free Follow-up Dependent 3")
                        .Run(() => Task.CompletedTask);
                });
            },
            operationName: "Blocking Follow-Up Child Operation");

        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Lock Holder")
                    .Run(RuntimeTestUtilities.RunUntilReleased(lockHolderStarted, releaseLockHolder), out lockHolderTaskId);

                /* This host task has no body of its own. Its imported child operation stays lock-free, but it becomes
                   ready from the same lock-holder completion that frees the waiting task's shared lock. */
                scope.Task("Long Lock-Free Follow-up")
                    .After(lockHolderTaskId)
                    .AddChildOperation(
                        blockingFollowUpOperation,
                        () =>
                        {
                            OperationParameters parameters = blockingFollowUpOperation.CreateParameters();
                            parameters.Target = new ExecutionTestCommon.TestTarget();
                            return parameters;
                        });

                scope.Task("Waiting Task", out waitingVisibleTaskId).Run(async context =>
                {
                    waitingTaskStarted.TrySetResult(context.TaskId);
                    await releaseWaitingTask.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return OperationResult.Succeeded();
                }, out waitingTaskId);
            });
        }, sharedLock);

        /* The waiting task is already ready from the start but cannot acquire the shared lock while the lock holder
           runs. Once the lock holder completes, the scheduler should retry the waiting task promptly even though the
           higher-priority lock-free follow-up becomes ready in the same scheduler pass. */
        (ExecutionPlan _, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);

        await lockHolderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(default, waitingTaskId);
        Assert.NotEqual(default, waitingVisibleTaskId);
        ExecutionTask waitingTask = session.GetTask(waitingVisibleTaskId);
        Assert.Equal(ExecutionTaskState.WaitingForExecutionLock, waitingTask.State);

        releaseLockHolder.TrySetResult(true);

        /* The waiting task should start promptly after the lock-holder body releases the shared lock even though the
           higher-priority follow-up task has already begun synchronous setup work. */
        TimeoutException? startTimeout = null;
        ExecutionTaskId startedTaskId = default;
        try
        {
            await followUpStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            startedTaskId = await waitingTaskStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException ex)
        {
            startTimeout = ex;
        }
        finally
        {
            releaseFollowUp.TrySetResult(true);
            releaseWaitingTask.TrySetResult(true);
        }

        OperationResult result = await executeTask;

        Assert.True(followUpStarted.Task.IsCompleted, "The synchronous lock-free follow-up never started.");
        Assert.Null(startTimeout);
        Assert.Equal(waitingTaskId, startedTaskId);
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
        ExecutionTaskId branchBVisibleTaskId = default;
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

                scope.Task("Branch B", out branchBVisibleTaskId).Children(branchScope =>
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

        // Assert: the visible/authored branch task that declared the lock should surface the explicit lock-wait state,
        // while its containing parallel scope stays Running because that subtree is already in progress.
        Assert.NotEqual(default, branchBVisibleTaskId);
        ExecutionTask branchBVisibleTask = session.GetTask(branchBVisibleTaskId);
        ExecutionTask branchBParallelScope = session.GetTask(branchBVisibleTask.ParentId!.Value);
        Assert.Equal(ExecutionTaskState.WaitingForExecutionLock, branchBVisibleTask.State);
        Assert.Equal(ExecutionTaskState.Running, branchBParallelScope.State);

        // Cleanup: release both branches in sequence so the run can complete if the scheduler eventually starts branch B.
        releaseBranchA.TrySetResult(true);
        await branchBStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        releaseBranchB.TrySetResult(true);
        await executeTask;
    }

    /// <summary>
    /// Confirms that a second session blocked only by an execution lock held by another live session remains pending
    /// instead of being marked skipped before that lock is released.
    /// </summary>
    [Fact]
    public async Task SecondSessionStaysPendingWhileAnotherSessionHoldsExecutionLock()
    {
        /* Each session contains one body task that declares the same shared execution lock. Session A keeps the lock open
           until the test releases it, while session B should remain non-terminal and pending until that happens. */
        ExecutionLock sharedLock = new("cross-session-lock");
        TaskCompletionSource<bool> sessionAStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseSessionA = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> sessionBStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseSessionB = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId sessionABodyTaskId = default;
        ExecutionTaskId sessionBBodyTaskId = default;

        Operation operationA = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(scope =>
            {
                scope.Task("Body").Run(RuntimeTestUtilities.RunUntilReleased(sessionAStarted, releaseSessionA), out sessionABodyTaskId);
            });
        }, sharedLock);

        Operation operationB = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(scope =>
            {
                scope.Task("Body").Run(async _ =>
                {
                    sessionBStarted.TrySetResult(true);
                    await releaseSessionB.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return OperationResult.Succeeded();
                }, out sessionBBodyTaskId);
            });
        }, sharedLock);

        /* Start session A first so it definitely holds the shared lock before session B begins. Session B should then be
           blocked only by that external lock holder. */
        (ExecutionPlan _, ExecutionSession sessionA, ExecutionPlanScheduler schedulerA) = RuntimeTestUtilities.CreateRuntime(operationA);
        (ExecutionPlan _, ExecutionSession sessionB, ExecutionPlanScheduler schedulerB) = RuntimeTestUtilities.CreateRuntime(operationB);
        Task<OperationResult> executeSessionA = schedulerA.ExecuteAsync(CancellationToken.None);
        await sessionAStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await sessionA.GetTask(sessionABodyTaskId).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Task<OperationResult> executeSessionB = schedulerB.ExecuteAsync(CancellationToken.None);

        try
        {
            /* Release session A and wait for it to finish so the shared execution lock becomes available again. Session B
               keeps its own body blocked on a separate gate, which means a correct implementation should start session B
               promptly after the release without allowing the session to complete yet. */
            releaseSessionA.TrySetResult(true);
            await executeSessionA;

            ExecutionTask sessionBBodyTask = sessionB.GetTask(sessionBBodyTaskId);
            await sessionBBodyTask.WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(sessionB.Completion.IsCompleted);
            Assert.Null(sessionB.Outcome);
            Assert.Equal(ExecutionTaskState.Running, sessionBBodyTask.State);
            Assert.Null(sessionBBodyTask.Outcome);
        }
        finally
        {
            /* Cleanup releases any remaining body gates and lets both schedulers drain. The assertions above remain the
               only intended failure point. */
            releaseSessionA.TrySetResult(true);
            releaseSessionB.TrySetResult(true);
            await Task.WhenAny(executeSessionA, Task.Delay(TimeSpan.FromSeconds(1)));
            await Task.WhenAny(executeSessionB, Task.Delay(TimeSpan.FromSeconds(1)));
        }
    }
}
