using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using TestUtilities;
using Xunit;

namespace LocalAutomation.Runtime.Tests;

public sealed class ExecutionTaskRuntimeStateRollupTests
{
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
    /// Confirms that a parent scope stays running when a direct child is running even if that child also contains a deeper
    /// execution-lock waiter.
    /// </summary>
    [Fact]
    public async Task StartedAncestorWithRunningChildAndLockWaitDescendantRollsUpRunning()
    {
        /* Arrange a three-level shape where Mixed Child is Running because one grandchild is active, while another
           grandchild is ready but blocked on the shared lock. */
        ExecutionLock sharedLock = new("running-child-lock-frontier");
        TaskCompletionSource<bool> lockHolderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseLockHolder = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> runningGrandchildStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseRunningGrandchild = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId lockHolderTaskId = default;
        ExecutionTaskId ancestorScopeTaskId = default;
        ExecutionTaskId mixedChildTaskId = default;
        ExecutionTaskId runningGrandchildTaskId = default;
        ExecutionTaskId lockWaitGrandchildTaskId = default;

        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Lock Holder")
                    .WithExecutionLocks(sharedLock)
                    .Run(RuntimeTestUtilities.RunUntilReleased(lockHolderStarted, releaseLockHolder), out lockHolderTaskId);

                scope.Task("Ancestor Scope", out ancestorScopeTaskId).Children(ancestorScope =>
                {
                    ancestorScope.Task("Mixed Child", out mixedChildTaskId).Children(ExecutionChildMode.Parallel, mixedChildScope =>
                    {
                        mixedChildScope.Task("Running Grandchild")
                            .Run(RuntimeTestUtilities.RunUntilReleased(runningGrandchildStarted, releaseRunningGrandchild), out runningGrandchildTaskId);

                        mixedChildScope.Task("Lock-Wait Grandchild")
                            .WithExecutionLocks(sharedLock)
                            .Run(() => Task.CompletedTask, out lockWaitGrandchildTaskId);
                    });
                });
            });
        });

        // Act: wait until the mixed child is running while its sibling grandchild is blocked on the shared lock.
        (ExecutionPlan _, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        try
        {
            await lockHolderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.GetTask(lockHolderTaskId).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await runningGrandchildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.GetTask(runningGrandchildTaskId).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await RuntimeTestUtilities.WaitForConditionAsync(
                () => session.GetTask(mixedChildTaskId).State == ExecutionTaskState.Running
                    && session.GetTask(lockWaitGrandchildTaskId).State == ExecutionTaskState.WaitingForExecutionLock,
                TimeSpan.FromSeconds(5),
                "Timed out waiting for the mixed child to be running while a grandchild waits for the shared lock.");

            // Assert: the ancestor must honor the direct child's Running state, not only the deeper lock wait.
            Assert.Equal(ExecutionTaskState.Running, session.GetTask(ancestorScopeTaskId).State);
        }
        finally
        {
            // Cleanup: release both gates so the scheduler can drain even when the red assertion fails.
            releaseLockHolder.TrySetResult(true);
            releaseRunningGrandchild.TrySetResult(true);
            await executeTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// Confirms that a started scope with one completed child, one child explicitly waiting for an execution lock, and
    /// one downstream queued child behind that lock waiter should still surface WaitingForExecutionLock instead of
    /// falling back to a generic running state.
    /// </summary>
    [Fact]
    public async Task StartedScopeWithLockWaitChildAndDownstreamQueuedChildRollsUpWaitingForExecutionLock()
    {
        /* Hold a shared execution lock outside the packaging scope so the middle child becomes the visible lock waiter
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
                            throw new InvalidOperationException($"Hidden child operation returned '{childResult.Outcome}'.");
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
}
