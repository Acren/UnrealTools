using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using TestUtilities;
using Xunit;

namespace LocalAutomation.Runtime.Tests;

public sealed class ExecutionPlanFailureAndCancellationTests
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
                        return OperationResult.Failed();
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
    /// Confirms that explicit user cancellation terminalizes a started scope that is currently waiting only on an
    /// external dependency instead of misclassifying that started scope as untouched skipped work.
    /// </summary>
    [Fact]
    public async Task ExplicitUserCancellationCancelsStartedDependencyWaitingScope()
    {
        /* Keep one unrelated task running until user cancellation so the second child remains dependency-blocked after
           the parent scope already started through its first child. That reproduces the same started-then-waiting shape
           seen in the Deploy Plugin cancellation failure without introducing a later timeout-driven failure path that
           would muddy the cancellation outcome being asserted here. */
        ExecutionTaskId blockerTaskId = default;
        ExecutionTaskId startedScopeTaskId = default;
        ExecutionTaskId completedChildTaskId = default;

        /* The minimal runtime shape is:
           - one active blocker outside the started scope,
           - one started scope with one completed child,
           - one later child still blocked on that external task.
           After the first child completes, the parent scope has started real work but its remaining frontier is only
           AwaitingDependency. */
        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Blocker").Run(RuntimeTestUtilities.RunUntilCancelled, out blockerTaskId);

                ExecutionTaskBuilder startedScope = scope.Task("Started Scope");
                startedScopeTaskId = startedScope.Id;
                startedScope.Children(childScope =>
                {
                    childScope.Task("Completed Child")
                        .Run(() => Task.CompletedTask, out completedChildTaskId);

                    childScope.Task("Blocked Child")
                        .After(blockerTaskId)
                        .Run(() => Task.CompletedTask);
                });
            });
        });

        /* Run the full session entry point so the test exercises the same cancellation and fatal-cleanup path the app
           uses in production instead of only the bare scheduler API. */
        ExecutionPlan plan = RuntimeTestUtilities.BuildPlan(operation);
        ExecutionSession session = new(new BufferedLogStream(), plan);
        Task runTask = session.RunAsync();

        /* Wait until the blocker is active and the first child already completed. That leaves the parent scope started,
           non-terminal, and currently waiting only on the external dependency. */
        await session.GetTask(blockerTaskId).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await session.GetTask(completedChildTaskId).WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(ExecutionTaskState.WaitingForDependencies, session.GetTask(startedScopeTaskId).State);

        /* User cancellation should complete the session cleanly and mark the started dependency-waiting scope as
           Cancelled, not Skipped. The current bug throws during cleanup before that terminal state is reached. */
        await session.CancelAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ExecutionTaskOutcome.Cancelled, session.GetTask(startedScopeTaskId).Outcome);
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
        ExecutionTask explodingBranchTask = session.GetTask(explodingBranchTaskId);
        explodingBranchTask.StateChanged += (_, state, _) =>
        {
            if (state == ExecutionTaskState.Running)
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
}
