using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
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
    /// Confirms that an unexpected scheduler-path exception should still terminalize the entire live graph instead of
    /// leaving already-started collateral work running after the root task is marked failed.
    /// </summary>
    [Fact]
    public async Task SchedulerExceptionShouldTerminalizeWholeGraph()
    {
        /* Keep one branch visibly active while the other branch triggers an exception from the scheduler path itself.
           The current broken behavior marks only the root failed, so this assertion should stay red until fatal-session
           cleanup also terminalizes the collateral branch. */
        TaskCompletionSource<bool> runningBranchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseRunningBranch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId runningBranchTaskId = default;
        ExecutionTaskId explodingBranchTaskId = default;
        const string injectedFailureMessage = "Injected scheduler failure.";

        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                /* This branch stays alive until the test releases it so the session definitely contains collateral active
                   work when the scheduler-path exception occurs. */
                scope.Task("Running Branch").Run(async _ =>
                {
                    runningBranchStarted.TrySetResult(true);
                    await releaseRunningBranch.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return OperationResult.Succeeded();
                }, out runningBranchTaskId);

                /* This branch itself succeeds, but the test injects a scheduler-path exception exactly when the session
                   publishes its Running transition. That keeps the failure out of the task body and inside session/
                   scheduler coordination. */
                scope.Task("Exploding Branch").Run(() => Task.FromResult(OperationResult.Succeeded()), out explodingBranchTaskId);
            });
        });

        ExecutionPlan plan = RuntimeTestUtilities.BuildPlan(operation);
        ExecutionSession session = new(new BufferedLogStream(), plan);
        session.TaskStateChanged += (taskId, state, _, _) =>
        {
            if (taskId == explodingBranchTaskId && state == ExecutionTaskState.Running)
            {
                throw new InvalidOperationException(injectedFailureMessage);
            }
        };

        Task runTask = session.RunAsync();

        try
        {
            /* Wait until the collateral branch is definitely active before asserting on the fatal-session cleanup state.
               Without this gate the scenario could pass vacuously if the scheduler failed before any collateral work
               started. */
            await runningBranchStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await runTask);
            Assert.Equal(injectedFailureMessage, exception.Message);

            /* The whole graph should be terminal after any fatal scheduler-path exception. This assertion is intentionally
               stronger than just checking the root so it stays red until collateral running work is also finalized. */
            Assert.Equal(ExecutionTaskOutcome.Failed, session.RootTask.Outcome);
            Assert.All(session.Tasks, task => Assert.Equal(ExecutionTaskState.Completed, task.State));
        }
        finally
        {
            /* Always release the blocked collateral branch so the red test does not leak a background task after the
               session-level assertion has observed the current broken state. */
            releaseRunningBranch.TrySetResult(true);
        }
    }

    /// <summary>
    /// Confirms that a task blocked only on an execution lock should surface the explicit lock-wait state until the
    /// scheduler can actually begin executing it.
    /// </summary>
    [Fact]
    public async Task TaskWaitingForExecutionLockStaysPending()
    {
        // Arrange: two parallel tasks contend for the same lock, and branch A holds it open.
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
                scope.Task("Branch A").WithExecutionLocks(sharedLock).Run(RuntimeTestUtilities.RunUntilReleased(branchAStarted, releaseBranchA), out branchATaskId);
                scope.Task("Branch B", out branchBVisibleTaskId).WithExecutionLocks(sharedLock).Run(_ => Task.FromResult(OperationResult.Succeeded()), out branchBTaskId);
            });
        });

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
        Assert.Equal(ExecutionTaskOutcome.Completed, session.GetTask(branchBVisibleTaskId).Outcome);
    }

    /// <summary>
    /// Confirms that a task waiting only on an execution lock still has no execution timer until it actually reaches
    /// Running.
    /// </summary>
    [Fact]
    public async Task TaskWaitingForExecutionLockDoesNotStartDuration()
    {
        // Arrange: branch A holds the shared lock so branch B can only reach the explicit lock-wait state.
        ExecutionLock sharedLock = new("lock-wait-duration");
        TaskCompletionSource<bool> branchAStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseBranchA = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId branchATaskId = default;
        ExecutionTaskId branchBVisibleTaskId = default;
        ExecutionTaskId branchBTaskId = default;

        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Branch A").WithExecutionLocks(sharedLock).Run(RuntimeTestUtilities.RunUntilReleased(branchAStarted, releaseBranchA), out branchATaskId);
                scope.Task("Branch B", out branchBVisibleTaskId).WithExecutionLocks(sharedLock).Run(_ => Task.FromResult(OperationResult.Succeeded()), out branchBTaskId);
            });
        });

        // Act: wait until branch A is definitely running so branch B is forced to remain in lock wait.
        (_, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        await branchAStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await session.GetTask(branchATaskId).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));

        // Assert: lock wait is visible, but elapsed execution time should still be absent.
        Assert.NotEqual(default, branchBVisibleTaskId);
        ExecutionTask blockedTask = session.GetTask(branchBVisibleTaskId);
        Assert.Equal(ExecutionTaskState.WaitingForExecutionLock, blockedTask.State);
        Assert.Null(blockedTask.StartedAt);
        Assert.Null(session.GetTaskDuration(branchBVisibleTaskId));

        // Cleanup: release the holder so the scheduler can finish and the test leaves no background work behind.
        releaseBranchA.TrySetResult(true);
        await executeTask;
    }

    /// <summary>
    /// Confirms that equal-priority siblings contending for the same execution lock should be admitted to lock wait one
    /// at a time in declared order instead of dispatching both worker executions and letting thread timing choose the
    /// eventual lock winner.
    /// </summary>
    [Fact]
    public async Task EqualPriorityLockContendersShouldBeAdmittedOneAtATimeInDeclaredOrder()
    {
        // Arrange: use three sibling child operations whose root body tasks each declare the same shared lock. The outer
        // test operation itself stays lock-free so the contention matches real production semantics more closely.
        ExecutionLock sharedLock = new("lock-admission-order");
        TaskCompletionSource<bool> lockHolderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseLockHolder = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> allowSecondInsertion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId lockHolderVisibleTaskId = default;
        ExecutionTaskId firstVisibleTaskId = default;
        ExecutionTaskId secondVisibleTaskId = default;

        Operation lockHolderOperation = new ExecutionTestCommon.InlineOperation(
            root =>
            {
                lockHolderVisibleTaskId = root.Id;
                root.WithExecutionLocks(sharedLock).Run(RuntimeTestUtilities.RunUntilReleased(lockHolderStarted, releaseLockHolder));
            },
            operationName: "Lock Holder Child Operation");

        Operation firstOperation = new ExecutionTestCommon.InlineOperation(
            root =>
            {
                firstVisibleTaskId = root.Id;
                root.WithExecutionLocks(sharedLock).Run(_ => Task.FromResult(OperationResult.Succeeded()));
            },
            operationName: "First Child Operation");

        Operation secondOperation = new ExecutionTestCommon.InlineOperation(
            root =>
            {
                secondVisibleTaskId = root.Id;
                root.WithExecutionLocks(sharedLock).Run(_ => Task.FromResult(OperationResult.Succeeded()));
            },
            operationName: "Second Child Operation");

        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Lock Holder")
                    .Run(async context =>
                    {
                        OperationParameters childParameters = lockHolderOperation.CreateParameters(context.ValidatedOperationParameters.CreateChild());
                        OperationResult childResult = await context.RunChildOperationAsync(lockHolderOperation, childParameters);
                        if (!childResult.Success)
                        {
                            throw new InvalidOperationException(childResult.FailureReason ?? "Lock holder child operation failed.");
                        }
                    });
                scope.Task("First")
                    .Run(async context =>
                    {
                        await lockHolderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        OperationParameters childParameters = firstOperation.CreateParameters(context.ValidatedOperationParameters.CreateChild());
                        OperationResult childResult = await context.RunChildOperationAsync(firstOperation, childParameters);
                        if (!childResult.Success)
                        {
                            throw new InvalidOperationException(childResult.FailureReason ?? "First child operation failed.");
                        }
                    });
                scope.Task("Second")
                    .Run(async context =>
                    {
                        await allowSecondInsertion.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        OperationParameters childParameters = secondOperation.CreateParameters(context.ValidatedOperationParameters.CreateChild());
                        OperationResult childResult = await context.RunChildOperationAsync(secondOperation, childParameters);
                        if (!childResult.Success)
                        {
                            throw new InvalidOperationException(childResult.FailureReason ?? "Second child operation failed.");
                        }
                    });
            });
        });

        // Act: start the scheduler, wait until the lock-holder child operation owns the shared lock, then insert the
        // second contender only after the declared-first contender is already the active waiter.
        (_, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        try
        {
            await lockHolderStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await session.GetTask(lockHolderVisibleTaskId).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));
            await RuntimeTestUtilities.WaitForConditionAsync(
                () => firstVisibleTaskId != default
                    && session.Tasks.Any(task => task.Id == firstVisibleTaskId)
                    && session.GetTask(firstVisibleTaskId).State == ExecutionTaskState.WaitingForExecutionLock,
                TimeSpan.FromSeconds(1),
                "Timed out waiting for the declared-first contender to enter explicit lock wait.");
            allowSecondInsertion.TrySetResult(true);
            await RuntimeTestUtilities.WaitForConditionAsync(
                () => secondVisibleTaskId != default && session.Tasks.Any(task => task.Id == secondVisibleTaskId),
                TimeSpan.FromSeconds(1),
                "Timed out waiting for the declared-second contender to be inserted.");

            // Assert: while the first child operation is already the active waiter for this lock set, the later second
            // child operation should remain pending and should not own an active worker execution yet.
            Assert.NotEqual(default, firstVisibleTaskId);
            Assert.NotEqual(default, secondVisibleTaskId);
            ExecutionTask firstVisibleTask = session.GetTask(firstVisibleTaskId);
            ExecutionTask secondVisibleTask = session.GetTask(secondVisibleTaskId);
            Assert.Equal(ExecutionTaskState.WaitingForExecutionLock, firstVisibleTask.State);
            Assert.True(firstVisibleTask.HasActiveExecution);
            Assert.Equal(ExecutionTaskState.Queued, secondVisibleTask.State);
            Assert.False(secondVisibleTask.HasActiveExecution);
        }
        finally
        {
            // Cleanup: always release the lock holder so the red test does not leave background work running after the
            // admission-state assertion fails.
            allowSecondInsertion.TrySetResult(true);
            releaseLockHolder.TrySetResult(true);
            await executeTask;
        }
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
                scope.Task("First").WithExecutionLocks(sharedLock).Run(async context =>
                {
                    firstStarted.TrySetResult(context.TaskId);
                    await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return OperationResult.Succeeded();
                }, out firstTaskId);
                scope.Task("Second").WithExecutionLocks(sharedLock).Run(async context =>
                {
                    firstStarted.TrySetResult(context.TaskId);
                    await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return OperationResult.Succeeded();
                }, out secondTaskId);
            });
        });

        // Act: execute once and capture whichever sibling body actually acquires the shared lock and starts first.
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
                    scope.Task("First").WithExecutionLocks(sharedLock).Run(async context =>
                    {
                        winnerSource.TrySetResult(context.TaskId);
                        await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        return OperationResult.Succeeded();
                    }, out firstTaskId);
                    scope.Task("Second").WithExecutionLocks(sharedLock).Run(async context =>
                    {
                        winnerSource.TrySetResult(context.TaskId);
                        await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        return OperationResult.Succeeded();
                    }, out secondTaskId);
                });
            });

            // Act: rebuild and run the same authored shape repeatedly, recording the sibling body that acquires the shared lock first.
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
                                lockHolderRoot.WithExecutionLocks(sharedLock).Run(async _ =>
                                {
                                    lockHolderStarted.TrySetResult(true);
                                    await releaseLockHolder.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                });
                            },
                            operationName: "Lock Holder Child Operation");

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
                                        .WithExecutionLocks(sharedLock)
                                        .Run(async childContext =>
                                        {
                                            contenderWinner.TrySetResult(childContext.TaskId);
                                            await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                        }, out branchAChildBodyTaskId);

                                    parallel.Task("Branch B")
                                        .WithExecutionLocks(sharedLock)
                                        .Run(async childContext =>
                                        {
                                            contenderWinner.TrySetResult(childContext.TaskId);
                                            await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                        }, out branchBChildBodyTaskId)
                                        .Then("Branch B Dependent")
                                        .Run(() => Task.CompletedTask);
                                });
                            },
                            operationName: "Contenders Child Operation");

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
        TaskCompletionSource<bool> releaseContenderGates = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> shortChildGateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> longChildGateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                                lockHolderRoot.WithExecutionLocks(sharedLock).Run(async _ =>
                                {
                                    lockHolderStarted.TrySetResult(true);
                                    await releaseLockHolder.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                });
                            },
                            operationName: "Lock Holder Child Operation");

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
                                        shortChildRoot.Children(childScope =>
                                        {
                                            /* Hold both inserted child operations at the same pre-lock boundary so the
                                               scheduler must choose between two simultaneously ready contenders instead of
                                               inheriting whichever child operation happened to finish inserting first. */
                                            ExecutionTaskBuilder shortReadyGate = childScope.Task("Ready Gate");
                                            shortReadyGate.Run(async _ =>
                                            {
                                                shortChildGateStarted.TrySetResult(true);
                                                await releaseContenderGates.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                            });

                                            shortReadyGate.Then("Lock Body")
                                                .WithExecutionLocks(sharedLock)
                                                .Run(async childContext =>
                                                {
                                                    contenderWinner.TrySetResult(childContext.TaskId);
                                                    await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                                }, out shortChildBodyTaskId);
                                        });
                                    },
                                    operationName: "Short Child Operation");

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
                                        longChildRoot.Children(childScope =>
                                        {
                                            /* Mirror the short branch's pre-lock gate so both concrete contenders
                                               become ready from the same explicit release point. */
                                            ExecutionTaskBuilder longReadyGate = childScope.Task("Ready Gate");
                                            longReadyGate.Run(async _ =>
                                            {
                                                longChildGateStarted.TrySetResult(true);
                                                await releaseContenderGates.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                            });

                                            longReadyGate.Then("Lock Body")
                                                .WithExecutionLocks(sharedLock)
                                                .Run(async childContext =>
                                                {
                                                    contenderWinner.TrySetResult(childContext.TaskId);
                                                    await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                                }, out longChildBodyTaskId);
                                        });
                                    },
                                    operationName: "Long Child Operation");

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

        /* Wait until the lock holder definitely owns the shared lock and both inserted child operations have reached the
           same explicit pre-lock gate. That isolates the arbitration point to the scheduler's downstream-priority choice
           instead of whichever child operation happened to finish inserting first. */
        (ExecutionPlan _, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        ExecutionTaskId winnerTaskId;
        try
        {
            await lockHolderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await shortChildGateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await longChildGateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await RuntimeTestUtilities.WaitForConditionAsync(
                () => shortChildRootTaskId != default
                    && longChildRootTaskId != default
                    && shortChildBodyTaskId != default
                    && longChildBodyTaskId != default
                    && session.Tasks.Any(task => task.Id == shortChildRootTaskId)
                    && session.Tasks.Any(task => task.Id == longChildRootTaskId)
                    && session.Tasks.Any(task => task.Id == shortChildBodyTaskId)
                    && session.Tasks.Any(task => task.Id == longChildBodyTaskId),
                TimeSpan.FromSeconds(5),
                "Timed out waiting for both inserted child operations to reach the shared-lock boundary.");

            Assert.NotEqual(default, shortChildBodyTaskId);
            Assert.NotEqual(default, longChildBodyTaskId);

            releaseContenderGates.TrySetResult(true);
            await RuntimeTestUtilities.WaitForConditionAsync(
                () => session.GetTask(longChildBodyTaskId).State == ExecutionTaskState.WaitingForExecutionLock,
                TimeSpan.FromSeconds(1),
                "Timed out waiting for the long branch contender to become the admitted lock waiter.");

            /* Once both contenders are ready together, the longer outer branch should be the one admitted into lock wait
               first. The shorter branch should still be queued behind that admitted waiter. */
            Assert.Equal(ExecutionTaskState.WaitingForExecutionLock, session.GetTask(longChildBodyTaskId).State);
            Assert.Equal(ExecutionTaskState.Queued, session.GetTask(shortChildBodyTaskId).State);

            releaseLockHolder.TrySetResult(true);
            winnerTaskId = await contenderWinner.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            releaseContenderGates.TrySetResult(true);
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
        /* Apply the shared lock only to the direct lock holder and waiting task so the higher-priority follow-up work
           remains lock-free in a separate child operation. That makes lock release and follow-up readiness happen on the
           same completed task without a parent waiting on its own same-lock child operation. */
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
                    .WithExecutionLocks(sharedLock)
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

                scope.Task("Waiting Task", out waitingVisibleTaskId).WithExecutionLocks(sharedLock).Run(async context =>
                {
                    waitingTaskStarted.TrySetResult(context.TaskId);
                    await releaseWaitingTask.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return OperationResult.Succeeded();
                }, out waitingTaskId);
            });
        });

        /* The waiting task is already ready from the start but cannot acquire the shared lock while the lock holder
           runs. Once the lock holder completes, the scheduler should retry the waiting task promptly even though the
           higher-priority lock-free follow-up becomes ready in the same scheduler pass. */
        (ExecutionPlan _, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);

        await lockHolderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
        Assert.Equal(waitingVisibleTaskId, startedTaskId);
        Assert.Equal(ExecutionTaskOutcome.Completed, result.Outcome);
    }

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

    /// <summary>
    /// Confirms that a container scope with no started work stays queued while its child is still blocked on an
    /// unsatisfied dependency.
    /// </summary>
    [Fact]
    public async Task ParentScopeStaysQueuedWhileChildWaitsForDependencies()
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

        // Assert: both the untouched waiting child and its untouched parent scope should still read as queued.
        ExecutionTask waitingChild = session.GetTask(waitingChildTaskId);
        ExecutionTask parentScope = session.GetTask(waitingChild.ParentId!.Value);
        Assert.Equal(ExecutionTaskState.Queued, waitingChild.State);
        Assert.Equal(ExecutionTaskState.Queued, parentScope.State);

        // Cleanup: release the dependency so the run can complete.
        releaseDependency.TrySetResult(true);
        await executeTask;
    }

    /// <summary>
    /// Confirms that the immediate parent branch of a lock-blocked child rolls up to the explicit lock-wait state, while
    /// a wider enclosing parallel scope still stays running when a sibling branch is actively executing.
    /// </summary>
    [Fact]
    public async Task ImmediateParentBranchRollsUpWaitingForExecutionLockWhileSharedParentKeepsRunning()
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
                    branchScope.Task("Active Work").WithExecutionLocks(sharedLock).Run(RuntimeTestUtilities.RunUntilReleased(branchAStarted, releaseBranchA), out branchABodyTaskId);
                });

                scope.Task("Branch B", out branchBVisibleTaskId).Children(branchScope =>
                {
                    branchScope.Task("Active Work").WithExecutionLocks(sharedLock).Run(RuntimeTestUtilities.RunUntilReleased(branchBStarted, releaseBranchB), out branchBBodyTaskId);
                });
            });
        });

        // Act: start the scheduler and wait until branch A is definitely running with the shared lock.
        (ExecutionPlan plan, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        await branchAStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await session.GetTask(branchABodyTaskId).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));

        // Assert: the real runnable child that declares the shared lock should surface the explicit lock-wait state, and
        // its immediate authored parent branch should bubble up the same blocker because nothing else in that branch can
        // run. The shared parent task above both branches must still stay Running because sibling branch A is actively
        // executing.
        Assert.NotEqual(default, branchBVisibleTaskId);
        ExecutionTask branchBVisibleTask = session.GetTask(branchBVisibleTaskId);
        ExecutionTask branchBActiveWorkTask = session.GetTask(branchBBodyTaskId);
        ExecutionTask sharedParentTask = session.GetTask(branchBVisibleTask.ParentId!.Value);
        Assert.Equal(ExecutionTaskState.WaitingForExecutionLock, branchBActiveWorkTask.State);
        Assert.Equal(ExecutionTaskState.WaitingForExecutionLock, branchBVisibleTask.State);
        Assert.Equal(ExecutionTaskState.Running, sharedParentTask.State);

        // Cleanup: release both branches in sequence so the run can complete if the scheduler eventually starts branch B.
        releaseBranchA.TrySetResult(true);
        await branchBStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        releaseBranchB.TrySetResult(true);
        await executeTask;
    }

    /// <summary>
    /// Confirms that a started scope with one completed child, one child explicitly waiting for an execution lock, and
    /// one downstream queued child behind that lock waiter should still surface WaitingForExecutionLock instead of
    /// falling back to a generic running state.
    /// </summary>
    [Fact]
    public async Task StartedScopeWithLockWaitChildAndDownstreamQueuedChildRollsUpWaitingForExecutionLock()
    {
        /* Hold a shared execution lock outside the packaging scope so the middle child becomes the admitted lock waiter
           while the final child remains merely queued behind that blocked work. */
        ExecutionLock sharedLock = new("scope-rollup-lock");
        TaskCompletionSource<bool> lockHolderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseLockHolder = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId lockHolderTaskId = default;
        ExecutionTaskId packagingScopeTaskId = default;
        ExecutionTaskId stageTaskId = default;
        ExecutionTaskId buildTaskId = default;
        ExecutionTaskId archiveTaskId = default;

        /* Keep the shape as small as possible:
           - Stage completes immediately,
           - Build is the only reachable frontier and waits on the shared lock,
           - Archive stays queued because it depends on Build finishing. */
        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Lock Holder")
                    .WithExecutionLocks(sharedLock)
                    .Run(RuntimeTestUtilities.RunUntilReleased(lockHolderStarted, releaseLockHolder), out lockHolderTaskId);

                scope.Task("Parent Scope", out packagingScopeTaskId).Children(parentScope =>
                {
                    parentScope.Task("Completed Child")
                        .Run(() => Task.CompletedTask, out stageTaskId);

                    parentScope.Task("Lock-Wait Child")
                        .After(stageTaskId)
                        .WithExecutionLocks(sharedLock)
                        .Run(() => Task.CompletedTask, out buildTaskId);

                    parentScope.Task("Downstream Queued Child")
                        .After(buildTaskId)
                        .Run(() => Task.CompletedTask, out archiveTaskId);
                });
            });
        });

        /* Wait until the external lock holder is definitely running, the first child already completed, and the build
           child is visibly blocked on the shared lock. At that point the archive child should still be queued behind the
           blocked build task, and the parent scope should surface the lock wait rather than a generic running state. */
        (ExecutionPlan _, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        try
        {
            await lockHolderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.GetTask(lockHolderTaskId).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await session.GetTask(stageTaskId).WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await RuntimeTestUtilities.WaitForConditionAsync(
                () => session.GetTask(buildTaskId).State == ExecutionTaskState.WaitingForExecutionLock,
                TimeSpan.FromSeconds(5),
                "Timed out waiting for the build child to enter explicit lock wait.");

            Assert.Equal(ExecutionTaskState.WaitingForExecutionLock, session.GetTask(buildTaskId).State);
            Assert.Equal(ExecutionTaskState.Queued, session.GetTask(archiveTaskId).State);
            Assert.Equal(ExecutionTaskState.WaitingForExecutionLock, session.GetTask(packagingScopeTaskId).State);
        }
        finally
        {
            /* Always release the external lock holder so a red assertion does not strand the background scheduler run. */
            releaseLockHolder.TrySetResult(true);
            await executeTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// Confirms that an executable parent task awaiting a hidden child operation returns to Running once that hidden
    /// child actually acquires its execution lock and starts running.
    /// </summary>
    [Fact]
    public async Task ExecutableParentReturnsToRunningAfterHiddenChildLeavesExecutionLockWait()
    {
        /* Hold the shared lock in one sibling branch first so the parent task's hidden child operation must initially
           surface WaitingForExecutionLock through the visible parent task before the child can later start running. */
        ExecutionLock sharedLock = new("hidden-child-running-parent-state-lock");
        TaskCompletionSource<bool> lockHolderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseLockHolder = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseHiddenChild = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId lockHolderTaskId = default;
        ExecutionTaskId parentVisibleTaskId = default;
        ExecutionTaskId hiddenChildRootTaskId = default;

        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Lock Holder")
                    .WithExecutionLocks(sharedLock)
                    .Run(RuntimeTestUtilities.RunUntilReleased(lockHolderStarted, releaseLockHolder), out lockHolderTaskId);

                scope.Task("Visible Parent Task", out parentVisibleTaskId)
                    .Run(async context =>
                    {
                        /* Keep the shape generic: the visible parent task is running its own body while the real
                           lock-taking work lives in a hidden child-operation root inserted beneath that parent. */
                        Operation hiddenChildOperation = new ExecutionTestCommon.InlineOperation(
                            childRoot =>
                            {
                                hiddenChildRootTaskId = childRoot.Id;
                                childRoot.WithExecutionLocks(sharedLock).Run(async _ =>
                                {
                                    await releaseHiddenChild.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                    return OperationResult.Succeeded();
                                });
                            },
                            operationName: "Hidden Child Operation");

                        OperationParameters hiddenChildParameters = hiddenChildOperation.CreateParameters(context.ValidatedOperationParameters.CreateChild());
                        OperationResult childResult = await context.RunChildOperationAsync(hiddenChildOperation, hiddenChildParameters, hideChildOperationRootInGraph: true);
                        if (!childResult.Success)
                        {
                            throw new InvalidOperationException(childResult.FailureReason ?? "Hidden child operation failed.");
                        }
                    });
            });
        });

        /* Start the scheduler, wait until the sibling lock holder is definitely active, then wait until the hidden child
           operation has been inserted and is visibly blocked on the shared execution lock through the parent task. */
        (ExecutionPlan _, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        try
        {
            await lockHolderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.GetTask(lockHolderTaskId).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await RuntimeTestUtilities.WaitForConditionAsync(
                () => hiddenChildRootTaskId != default
                    && session.Tasks.Any(task => task.Id == hiddenChildRootTaskId)
                    && session.GetTask(hiddenChildRootTaskId).State == ExecutionTaskState.WaitingForExecutionLock
                    && session.GetTask(parentVisibleTaskId).State == ExecutionTaskState.WaitingForExecutionLock,
                TimeSpan.FromSeconds(5),
                "Timed out waiting for the hidden child operation to enter explicit lock wait through the visible parent.");

            /* Once the lock holder releases the shared lock, the hidden child should enter Running. The visible parent is
               still executing its own body while awaiting that child result, so it should return to Running instead of
               remaining stuck in the rolled-up WaitingForExecutionLock state. */
            releaseLockHolder.TrySetResult(true);
            await RuntimeTestUtilities.WaitForConditionAsync(
                () => session.GetTask(hiddenChildRootTaskId).State == ExecutionTaskState.Running,
                TimeSpan.FromSeconds(5),
                "Timed out waiting for the hidden child operation to start running after the shared lock was released.");

            Assert.Equal(ExecutionTaskState.Running, session.GetTask(parentVisibleTaskId).State);
        }
        finally
        {
            /* Always release every held branch so a red assertion does not strand the background scheduler run. */
            releaseLockHolder.TrySetResult(true);
            releaseHiddenChild.TrySetResult(true);
            await executeTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
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
                scope.Task("Body").WithExecutionLocks(sharedLock).Run(RuntimeTestUtilities.RunUntilReleased(sessionAStarted, releaseSessionA), out sessionABodyTaskId);
            });
        });

        Operation operationB = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(scope =>
            {
                scope.Task("Body").WithExecutionLocks(sharedLock).Run(async _ =>
                {
                    sessionBStarted.TrySetResult(true);
                    await releaseSessionB.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return OperationResult.Succeeded();
                }, out sessionBBodyTaskId);
            });
        });

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
