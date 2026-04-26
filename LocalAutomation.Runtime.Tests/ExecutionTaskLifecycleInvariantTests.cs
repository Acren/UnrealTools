using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LocalAutomation.Runtime.Tests;

public sealed class ExecutionTaskLifecycleInvariantTests
{
    /// <summary>
    /// Confirms that a completed task's cached duration remains frozen at its completion boundary instead of advancing
    /// when callers ask for metrics with a later clock value.
    /// </summary>
    [Fact]
    public async Task CompletedTaskDurationDoesNotAdvanceAfterCompletion()
    {
        ExecutionTaskId timedTaskId = default;

        /* A single runnable task is the smallest runtime shape that exercises the start timestamp, completion timestamp,
           and cached subtree timing basis used by task metric reads. */
        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(scope =>
            {
                scope.Task("Timed Work").Run(() => Task.CompletedTask, out timedTaskId);
            });
        });

        /* Execute through the real scheduler so the task reaches Running and then Completed through normal runtime
           transitions before the duration is read back. */
        (ExecutionPlan _, ExecutionSession session, OperationResult result) = await RuntimeTestUtilities.ExecuteAsync(operation);
        ExecutionTask timedTask = session.GetTask(timedTaskId);
        DateTimeOffset finishedAt = timedTask.FinishedAt ?? throw new InvalidOperationException("The timed task did not record a finish timestamp.");

        /* The task completed successfully, so any later metric read should reuse the recorded finish timestamp instead of
           treating the completed task as still actively timed. */
        TimeSpan? durationAtCompletion = session.GetTaskDuration(timedTaskId, finishedAt);
        TimeSpan? durationAfterCompletion = session.GetTaskDuration(timedTaskId, finishedAt + TimeSpan.FromMinutes(10));

        Assert.Equal(ExecutionTaskOutcome.Completed, result.Outcome);
        Assert.Equal(ExecutionTaskState.Completed, timedTask.State);
        Assert.NotNull(durationAtCompletion);
        Assert.Equal(durationAtCompletion, durationAfterCompletion);
    }

    /// <summary>
    /// Confirms that once a scope has started and its only prerequisite has completed, the scope should surface
    /// WaitingForDependencies when its remaining reachable work is blocked only by an unfinished task outside the scope.
    /// </summary>
    [Fact]
    public async Task StartedScopeWithSatisfiedPrerequisiteWaitsForDependencies()
    {
        /* Keep one unrelated task running so the blocked child stays non-terminal after the scope already started. */
        TaskCompletionSource<bool> releaseBlocker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId blockerTaskId = default;
        ExecutionTaskId prepareSharedSourceTaskId = default;
        ExecutionTaskId startedScopeTaskId = default;
        ExecutionTaskId completedChildTaskId = default;

        /* The minimal shape for this invariant is:
           - one prerequisite,
           - one started scope that depends only on that prerequisite,
           - one completed child,
           - one later child still blocked on unrelated work. */
        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                ExecutionTaskBuilder blocker = scope.Task("Blocker");
                blockerTaskId = blocker.Id;
                blocker.Run(async _ =>
                {
                    await releaseBlocker.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return OperationResult.Succeeded();
                });

                ExecutionTaskBuilder prerequisite = scope.Task("Prerequisite");
                prepareSharedSourceTaskId = prerequisite.Id;
                prerequisite.Run(() => Task.CompletedTask);

                ExecutionTaskBuilder startedScope = scope.Task("Started Scope");
                startedScopeTaskId = startedScope.Id;
                startedScope.After(prepareSharedSourceTaskId).Children(childScope =>
                {
                    ExecutionTaskBuilder completedChild = childScope.Task("Completed Child");
                    completedChildTaskId = completedChild.Id;
                    completedChild.Run(() => Task.CompletedTask);

                    childScope.Task("Blocked Child")
                        .After(blockerTaskId)
                        .Run(() => Task.CompletedTask);
                });
            });
        });

        /* Wait until the blocker is running and both prerequisite plus first child have already completed. That leaves
           the started scope non-terminal but momentarily idle, which is exactly the illegal post-start queued case. */
        (ExecutionPlan _, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        try
        {
            await session.GetTask(blockerTaskId).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));
            await session.GetTask(prepareSharedSourceTaskId).WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(1));
            await session.GetTask(completedChildTaskId).WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(1));

            /* The prerequisite is already terminal and the scope already started real descendant work, so the scope is no
               longer merely queued. With no local work still active, the remaining external blocker should surface as
               WaitingForDependencies. */
            Assert.Equal(ExecutionTaskOutcome.Completed, session.GetTask(prepareSharedSourceTaskId).Outcome);
            Assert.Equal(ExecutionTaskState.WaitingForDependencies, session.GetTask(startedScopeTaskId).State);
        }
        finally
        {
            /* Always release the blocker so a failing assertion does not strand the background scheduler run. */
            releaseBlocker.TrySetResult(true);
        }

        /* After cleanup releases the blocker, the run should still finish successfully. */
        OperationResult result = await executeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ExecutionTaskOutcome.Completed, result.Outcome);
    }

    /// <summary>
    /// Confirms that once a parent scope has started and one child already completed, the parent should surface
    /// WaitingForDependencies when its only remaining child work is blocked by an unfinished task outside that parent.
    /// </summary>
    [Fact]
    public async Task StartedParentScopeWaitsForDependenciesAfterChildCompletes()
    {
        /* Keep one unrelated task running so the second child remains blocked after the first child already finished. */
        TaskCompletionSource<bool> releaseBlocker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId blockerTaskId = default;
        ExecutionTaskId parentScopeTaskId = default;
        ExecutionTaskId completedChildTaskId = default;

        /* This is the smallest shape for the parent-child invariant:
           - one parent scope,
           - one completed child,
           - one later child still blocked on unrelated work. */
        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                ExecutionTaskBuilder blocker = scope.Task("Blocker");
                blockerTaskId = blocker.Id;
                blocker.Run(async _ =>
                {
                    await releaseBlocker.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return OperationResult.Succeeded();
                });

                ExecutionTaskBuilder parentScope = scope.Task("Parent Scope");
                parentScopeTaskId = parentScope.Id;
                parentScope.Children(childScope =>
                {
                    ExecutionTaskBuilder completedChild = childScope.Task("Completed Child");
                    completedChildTaskId = completedChild.Id;
                    completedChild.Run(() => Task.CompletedTask);

                    childScope.Task("Blocked Child")
                        .After(blockerTaskId)
                        .Run(() => Task.CompletedTask);
                });
            });
        });

        /* Wait until the blocker is running and the first child already completed. That leaves the parent scope started,
           non-terminal, and locally idle because its only remaining child work is blocked on an external dependency. */
        (ExecutionPlan _, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        try
        {
            await session.GetTask(blockerTaskId).WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(1));
            await session.GetTask(completedChildTaskId).WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(1));

            /* The parent already has completed child work, so it is no longer merely queued. With no local running work
               left, its current blocker should surface as dependency wait instead of a generic running state. */
            Assert.Equal(ExecutionTaskOutcome.Completed, session.GetTask(completedChildTaskId).Outcome);
            Assert.Equal(ExecutionTaskState.WaitingForDependencies, session.GetTask(parentScopeTaskId).State);
        }
        finally
        {
            /* Always release the blocker so a failing assertion does not strand the background scheduler run. */
            releaseBlocker.TrySetResult(true);
        }

        /* After cleanup releases the blocker, the run should still finish successfully. */
        OperationResult result = await executeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ExecutionTaskOutcome.Completed, result.Outcome);
    }
}
