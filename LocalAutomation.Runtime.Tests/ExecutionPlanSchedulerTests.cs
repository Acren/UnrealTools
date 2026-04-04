using System;
using System.Threading;
using System.Threading.Tasks;
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
        ExecutionTaskHandle branchAActiveHandle = default;
        ExecutionTaskHandle branchBActiveHandle = default;
        ExecutionTaskHandle branchBQueuedCallbackHandle = default;

        /* The plan shape mirrors the production scenario in the smallest possible form:
           - Branch A has one active callback that will fail and one queued callback that never matters.
           - Branch B has one active callback that only stops when cancellation reaches it and one queued callback that
             should stay untouched.
           When Branch A fails, Branch B.Active Work is already running and Branch B.Queued Work is still pending. */
        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Branch A").Children(branchAScope =>
                {
                    /* Capture the execution handle directly from Run(..., out ...) so the test can await the live runtime
                       task by identity without rediscovering the callback node later. */
                    branchAScope.Task("Active Work").Run(async _ =>
                    {
                        await allowBranchAFailure.Task.WaitAsync(TimeSpan.FromSeconds(1));
                        return OperationResult.Failed(failureReason: "Branch A failed.");
                    }, out branchAActiveHandle);
                    branchAScope.Task("Queued Work").Run(() => Task.FromResult(OperationResult.Succeeded()));
                });

                scope.Task("Branch B").Children(branchBScope =>
                {
                    /* This callback blocks forever until the scheduler cancels it. That makes it the collateral running
                       work whose semantic outcome should become Interrupted, not Cancelled, when Branch A fails. */
                    branchBScope.Task("Active Work").Run(RuntimeTestUtilities.RunUntilCancelled, out branchBActiveHandle);
                    branchBScope.Task("Queued Work").Run(context =>
                    {
                        return Task.FromResult(OperationResult.Succeeded());
                    }, out branchBQueuedCallbackHandle);
                });
            });
        });

        /* This scenario needs direct scheduler control so the test can wait for both active tasks to reach Running before
           allowing Branch A to fail. */
        (ExecutionPlan plan, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(plan, CancellationToken.None);
        await session.GetTask(branchAActiveHandle.Id).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await session.GetTask(branchBActiveHandle.Id).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));
        allowBranchAFailure.TrySetResult(true);

        OperationResult result = await executeTask;

        Assert.True(branchAActiveHandle.IsValid);
        Assert.True(branchBActiveHandle.IsValid);
        Assert.True(branchBQueuedCallbackHandle.IsValid);

        /* Expected outcomes:
           - overall run fails because Branch A failed directly,
           - Branch A.Active Work is Failed,
           - Branch B.Active Work is Interrupted because sibling failure stopped it,
           - Branch B.Queued Work is Skipped because it never started. */
        Assert.Equal(ExecutionTaskOutcome.Failed, result.Outcome);
        Assert.Equal(ExecutionTaskOutcome.Failed, session.GetTask(branchAActiveHandle.Id).Outcome);
        Assert.Equal(ExecutionTaskOutcome.Interrupted, session.GetTask(branchBActiveHandle.Id).Outcome);
        Assert.Equal(ExecutionTaskOutcome.Skipped, session.GetTask(branchBQueuedCallbackHandle.Id).Outcome);
    }

    /// <summary>
    /// Confirms that explicit user cancellation still produces Cancelled for active work while queued work remains Skipped.
    /// </summary>
    [Fact]
    public async Task ExplicitUserCancellationKeepsActiveWorkCancelled()
    {
        /* Both active branches block until the test cancels the scheduler token, which isolates the user-cancel path from
           the sibling-failure interruption path above. */
        ExecutionTaskHandle branchAActiveHandle = default;
        ExecutionTaskHandle branchBActiveHandle = default;
        ExecutionTaskHandle branchAQueuedCallbackHandle = default;

        /* This scenario uses the same two-branch shape, but neither branch fails on its own.
           The only stop signal comes from the explicit cancellation token passed to ExecuteAsync. That means active work
           should stay Cancelled rather than Interrupted. */
        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Branch A").Children(branchAScope =>
                {
                    /* Capture the active execution handle so the test can await the real runtime task reaching Running
                       before it cancels the session. */
                    branchAScope.Task("Active Work").Run(RuntimeTestUtilities.RunUntilCancelled, out branchAActiveHandle);
                    branchAScope.Task("Queued Work").Run(context =>
                    {
                        return Task.FromResult(OperationResult.Succeeded());
                    }, out branchAQueuedCallbackHandle);
                });

                scope.Task("Branch B").Children(branchBScope =>
                {
                    branchBScope.Task("Active Work").Run(RuntimeTestUtilities.RunUntilCancelled, out branchBActiveHandle);
                });
            });
        });

        /* This case still needs the authored plan before execution only because the scheduler is started manually so the
           test can cancel it at the right time. The queued callback id is captured directly from Run(..., out handle), so
           no string-based plan lookup is needed. */
        (ExecutionPlan plan, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        using CancellationTokenSource cancellationSource = new();

        /* Wait until both active branches are definitely running before cancelling so the assertions exercise active-work
           cancellation semantics instead of startup-time skipping. */
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(plan, cancellationSource.Token);
        await session.GetTask(branchAActiveHandle.Id).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await session.GetTask(branchBActiveHandle.Id).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));
        cancellationSource.Cancel();

        OperationResult result = await executeTask;

        Assert.True(branchAActiveHandle.IsValid);
        Assert.True(branchBActiveHandle.IsValid);
        Assert.True(branchAQueuedCallbackHandle.IsValid);

        /* Expected outcomes:
           - overall run is Cancelled because the user token ended the run,
           - both active callbacks are Cancelled,
           - the untouched queued callback is Skipped because it never started. */
        Assert.Equal(ExecutionTaskOutcome.Cancelled, result.Outcome);
        Assert.Equal(ExecutionTaskOutcome.Cancelled, session.GetTask(branchAActiveHandle.Id).Outcome);
        Assert.Equal(ExecutionTaskOutcome.Cancelled, session.GetTask(branchBActiveHandle.Id).Outcome);
        Assert.Equal(ExecutionTaskOutcome.Skipped, session.GetTask(branchAQueuedCallbackHandle.Id).Outcome);
    }
}
