using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using TestUtilities;
using Xunit;

namespace LocalAutomation.Runtime.Tests;

public sealed class ExecutionLockWaitTests
{
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
    /// Confirms that equal-priority siblings contending for the same execution lock should wait without active workers and
    /// then acquire the lock in declared order.
    /// </summary>
    [Fact]
    public async Task EqualPriorityLockWaitersStartInDeclaredOrder()
    {
        // Arrange: use three sibling child operations whose root body tasks each declare the same shared lock. The outer
        // test operation itself stays lock-free so the contention matches real production semantics more closely.
        ExecutionLock sharedLock = new("lock-admission-order");
        TaskCompletionSource<bool> lockHolderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseLockHolder = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> allowSecondInsertion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ExecutionTaskId> lockWinner = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                root.WithExecutionLocks(sharedLock).Run(context =>
                {
                    lockWinner.TrySetResult(context.TaskId);
                    return Task.FromResult(OperationResult.Succeeded());
                });
            },
            operationName: "First Child Operation");

        Operation secondOperation = new ExecutionTestCommon.InlineOperation(
            root =>
            {
                secondVisibleTaskId = root.Id;
                root.WithExecutionLocks(sharedLock).Run(context =>
                {
                    lockWinner.TrySetResult(context.TaskId);
                    return Task.FromResult(OperationResult.Succeeded());
                });
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
                            throw new InvalidOperationException($"Lock holder child operation returned '{childResult.Outcome}'.");
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
                            throw new InvalidOperationException($"First child operation returned '{childResult.Outcome}'.");
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
                            throw new InvalidOperationException($"Second child operation returned '{childResult.Outcome}'.");
                        }
                    });
            });
        });

        // Act: start the scheduler, wait until the lock-holder child operation owns the shared lock, then insert the
        // second contender only after the declared-first contender is already waiting for that lock.
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
                () => secondVisibleTaskId != default
                    && session.Tasks.Any(task => task.Id == secondVisibleTaskId)
                    && session.GetTask(secondVisibleTaskId).State == ExecutionTaskState.WaitingForExecutionLock,
                TimeSpan.FromSeconds(1),
                "Timed out waiting for the declared-second contender to enter explicit lock wait.");

            // Assert: both contenders are lock waiters, and neither owns an active worker before the lock is available.
            Assert.NotEqual(default, firstVisibleTaskId);
            Assert.NotEqual(default, secondVisibleTaskId);
            ExecutionTask firstVisibleTask = session.GetTask(firstVisibleTaskId);
            ExecutionTask secondVisibleTask = session.GetTask(secondVisibleTaskId);
            Assert.Equal(ExecutionTaskState.WaitingForExecutionLock, firstVisibleTask.State);
            Assert.False(firstVisibleTask.HasActiveExecution);
            Assert.Equal(ExecutionTaskState.WaitingForExecutionLock, secondVisibleTask.State);
            Assert.False(secondVisibleTask.HasActiveExecution);

            // Assert: once the lock is released, equal-priority waiters should acquire in first-registration order.
            releaseLockHolder.TrySetResult(true);
            ExecutionTaskId winnerTaskId = await lockWinner.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(firstVisibleTaskId, winnerTaskId);
        }
        finally
        {
            // Cleanup: always release the lock holder so a failing assertion does not leave background work running.
            allowSecondInsertion.TrySetResult(true);
            releaseLockHolder.TrySetResult(true);
            await executeTask;
        }
    }

    /// <summary>
    /// Confirms that an execution lock declared on a container task protects the authored child subtree, not only a direct
    /// task body. A same-lock sibling must wait until the container's child work completes.
    /// </summary>
    [Fact]
    public async Task ExecutionLockDeclaredOnContainerProtectsChildSubtree()
    {
        // Arrange: the parent declares the shared lock, while its child performs the actual long-running work.
        ExecutionLock sharedLock = new("container-scope-lock");
        TaskCompletionSource<bool> childStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseChild = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> contenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseContender = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId contenderTaskId = default;

        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Locked Parent")
                    .WithExecutionLocks(sharedLock)
                    .Children(children =>
                    {
                        children.Task("Child Work")
                            .Run(RuntimeTestUtilities.RunUntilReleased(childStarted, releaseChild));
                    });

                scope.Task("Same-Lock Contender", out contenderTaskId)
                    .WithExecutionLocks(sharedLock)
                    .Run(async _ =>
                    {
                        contenderStarted.TrySetResult(true);
                        await releaseContender.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        return OperationResult.Succeeded();
                    });
            });
        });

        // Act: start the parent child work, then wait until the contender either blocks correctly or exposes the bug by running.
        (_, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);

        try
        {
            await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await RuntimeTestUtilities.WaitForConditionAsync(
                () => contenderStarted.Task.IsCompleted
                    || session.GetTask(contenderTaskId).State == ExecutionTaskState.WaitingForExecutionLock,
                TimeSpan.FromSeconds(1),
                "Timed out waiting for the same-lock contender to either run or enter lock wait.");

            // Assert: current buggy behavior starts the contender because the parent container's lock is inert.
            Assert.False(contenderStarted.Task.IsCompleted);
            Assert.Equal(ExecutionTaskState.WaitingForExecutionLock, session.GetTask(contenderTaskId).State);
        }
        finally
        {
            // Cleanup: release both gates so a red assertion cannot strand the background scheduler.
            releaseChild.TrySetResult(true);
            releaseContender.TrySetResult(true);
            await executeTask;
        }
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

                /* This host task has no body of its own. Its scoped child operation stays lock-free, but it becomes ready
                   from the same lock-holder completion that frees the waiting task's shared lock. */
                scope.Task("Long Lock-Free Follow-up")
                    .After(lockHolderTaskId)
                    .Children(followUpScope =>
                    {
                        followUpScope.AddChildOperation(
                            blockingFollowUpOperation,
                            () =>
                            {
                                OperationParameters parameters = blockingFollowUpOperation.CreateParameters();
                                parameters.Target = new ExecutionTestCommon.TestTarget();
                                return parameters;
                            });
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
