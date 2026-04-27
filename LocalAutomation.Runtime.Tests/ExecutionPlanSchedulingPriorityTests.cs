using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using TestUtilities;
using Xunit;

namespace LocalAutomation.Runtime.Tests;

public sealed class ExecutionPlanSchedulingPriorityTests
{
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
                            throw new InvalidOperationException($"Lock holder child operation returned '{lockHolderResult.Outcome}'.");
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
                            throw new InvalidOperationException($"Contenders child operation returned '{contendersResult.Outcome}'.");
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
    /// Confirms that lock release should rank all currently waiting contenders instead of committing to the contender that
    /// reached the lock boundary first.
    /// </summary>
    [Fact]
    public async Task LockReleaseReprioritizesLaterHigherPriorityContender()
    {
        // Arrange: keep the shared lock held while a lower-priority contender reaches the lock boundary first.
        ExecutionLock sharedLock = new("reprioritize-lock-release");
        TaskCompletionSource<bool> lockHolderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseLockHolder = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> allowHigherPriorityInsertion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ExecutionTaskId> lockWinner = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseWinner = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId lowerPriorityBodyTaskId = default;
        ExecutionTaskId higherPriorityBodyTaskId = default;

        // Arrange: the lower-priority child has no downstream work after it wins the lock.
        Operation lowerPriorityOperation = new ExecutionTestCommon.InlineOperation(
            childRoot =>
            {
                childRoot.Child("Lower Priority Contender")
                    .WithExecutionLocks(sharedLock)
                    .Run(async context =>
                    {
                        lockWinner.TrySetResult(context.TaskId);
                        await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }, out lowerPriorityBodyTaskId);
            },
            operationName: "Lower Priority Child Operation");

        // Arrange: the later child has one dependent task, so the scheduler's existing downstream-work rule should rank it higher.
        Operation higherPriorityOperation = new ExecutionTestCommon.InlineOperation(
            childRoot =>
            {
                childRoot.Child("Higher Priority Contender")
                    .WithExecutionLocks(sharedLock)
                    .Run(async context =>
                    {
                        lockWinner.TrySetResult(context.TaskId);
                        await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }, out higherPriorityBodyTaskId)
                    .Then("Higher Priority Dependent")
                    .Run(() => Task.CompletedTask);
            },
            operationName: "Higher Priority Child Operation");

        // Arrange: insert the higher-priority contender only after the lower-priority contender is already waiting.
        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Lock Holder")
                    .WithExecutionLocks(sharedLock)
                    .Run(RuntimeTestUtilities.RunUntilReleased(lockHolderStarted, releaseLockHolder));

                scope.Task("Insert Lower Priority Contender")
                    .Run(async context =>
                    {
                        await lockHolderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        OperationParameters parameters = lowerPriorityOperation.CreateParameters(context.ValidatedOperationParameters.CreateChild());
                        OperationResult result = await context.RunChildOperationAsync(lowerPriorityOperation, parameters);
                        if (!result.Success)
                        {
                            throw new InvalidOperationException($"Lower-priority child operation returned '{result.Outcome}'.");
                        }
                    });

                scope.Task("Insert Higher Priority Contender")
                    .Run(async context =>
                    {
                        await allowHigherPriorityInsertion.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        OperationParameters parameters = higherPriorityOperation.CreateParameters(context.ValidatedOperationParameters.CreateChild());
                        OperationResult result = await context.RunChildOperationAsync(higherPriorityOperation, parameters);
                        if (!result.Success)
                        {
                            throw new InvalidOperationException($"Higher-priority child operation returned '{result.Outcome}'.");
                        }
                    });
            });
        });

        // Act: wait until both contenders exist while the original holder still owns the lock, then release the lock.
        (ExecutionPlan _, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        try
        {
            await lockHolderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await RuntimeTestUtilities.WaitForConditionAsync(
                () => lowerPriorityBodyTaskId != default
                    && session.Tasks.Any(task => task.Id == lowerPriorityBodyTaskId)
                    && session.GetTask(lowerPriorityBodyTaskId).State == ExecutionTaskState.WaitingForExecutionLock,
                TimeSpan.FromSeconds(5),
                "Timed out waiting for the lower-priority contender to reach the lock boundary.");

            allowHigherPriorityInsertion.TrySetResult(true);
            await RuntimeTestUtilities.WaitForConditionAsync(
                () => higherPriorityBodyTaskId != default
                    && session.Tasks.Any(task => task.Id == higherPriorityBodyTaskId),
                TimeSpan.FromSeconds(5),
                "Timed out waiting for the higher-priority contender to be inserted before lock release.");

            releaseLockHolder.TrySetResult(true);
            ExecutionTaskId winnerTaskId = await lockWinner.Task.WaitAsync(TimeSpan.FromSeconds(1));

            // Assert: lock release should choose the later higher-priority contender because it unlocks more downstream work.
            Assert.NotEqual(default, higherPriorityBodyTaskId);
            Assert.Equal(higherPriorityBodyTaskId, winnerTaskId);
        }
        finally
        {
            // Cleanup: open every gate so the scheduler can drain even while this red test is failing on the winner assertion.
            allowHigherPriorityInsertion.TrySetResult(true);
            releaseLockHolder.TrySetResult(true);
            releaseWinner.TrySetResult(true);
            await executeTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
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
        ExecutionTask shortBranchTask = session.GetTask(shortBranchTaskId);
        ExecutionTask longBranchTask = session.GetTask(longBranchTaskId);
        shortBranchTask.StateChanged += OnTaskStateChanged;
        longBranchTask.StateChanged += OnTaskStateChanged;
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        try
        {
            await RuntimeTestUtilities.WaitForConditionAsync(() => firstStartedTaskId != default, TimeSpan.FromSeconds(1));
        }
        finally
        {
            shortBranchTask.StateChanged -= OnTaskStateChanged;
            longBranchTask.StateChanged -= OnTaskStateChanged;
            releaseWinner.TrySetResult(true);
        }

        OperationResult result = await executeTask;

        Assert.NotEqual(default, shortBranchTaskId);
        Assert.NotEqual(default, longBranchTaskId);
        Assert.Equal(longBranchTaskId, firstStartedTaskId);
        Assert.Equal(ExecutionTaskOutcome.Completed, result.Outcome);

        void OnTaskStateChanged(ExecutionTask task, ExecutionTaskState state, ExecutionTaskOutcome? _)
        {
            if (state != ExecutionTaskState.Running || firstStartedTaskId != default)
            {
                return;
            }

            if (task.Id == shortBranchTaskId || task.Id == longBranchTaskId)
            {
                firstStartedTaskId = task.Id;
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
                            throw new InvalidOperationException($"Lock holder child operation returned '{lockHolderResult.Outcome}'.");
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
                                    throw new InvalidOperationException($"Short child operation returned '{shortChildResult.Outcome}'.");
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
                                    throw new InvalidOperationException($"Long child operation returned '{longChildResult.Outcome}'.");
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
                () => session.GetTask(longChildBodyTaskId).State == ExecutionTaskState.WaitingForExecutionLock
                    && session.GetTask(shortChildBodyTaskId).State == ExecutionTaskState.WaitingForExecutionLock,
                TimeSpan.FromSeconds(1),
                "Timed out waiting for both branch contenders to enter explicit lock wait.");

            /* Once both contenders are ready together, both should wait for the lock without active workers. The longer
               outer branch should still win when the lock becomes available. */
            Assert.Equal(ExecutionTaskState.WaitingForExecutionLock, session.GetTask(longChildBodyTaskId).State);
            Assert.False(session.GetTask(longChildBodyTaskId).HasActiveExecution);
            Assert.Equal(ExecutionTaskState.WaitingForExecutionLock, session.GetTask(shortChildBodyTaskId).State);
            Assert.False(session.GetTask(shortChildBodyTaskId).HasActiveExecution);

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
    /// Confirms that lock priority counts downstream work unlocked when an intermediate parent scope completes.
    /// </summary>
    [Fact]
    public async Task LockReleaseCountsDownstreamWorkThroughParentScopeCompletion()
    {
        /* Keep the shared lock held while both contenders reach explicit lock wait. The older package-like contender
           unlocks follow-up work through an intermediate parent scope, while the later build-like contender sits inside
           the comparable parent scope and can be over-valued if only ancestor dependency edges are counted. */
        ExecutionLock sharedLock = new("parent-scope-downstream-priority-lock");
        TaskCompletionSource<bool> lockHolderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseLockHolder = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> allowLaterBranch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ExecutionTaskId> lockWinner = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseWinner = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionTaskId olderPackageTaskId = default;
        ExecutionTaskId laterBuildTaskId = default;

        Operation operation = new RuntimeTestUtilities.InlineOperation(root =>
        {
            root.Children(ExecutionChildMode.Parallel, scope =>
            {
                scope.Task("Lock Holder")
                    .WithExecutionLocks(sharedLock)
                    .Run(RuntimeTestUtilities.RunUntilReleased(lockHolderStarted, releaseLockHolder));

                ExecutionTaskBuilder contenderGate = scope.Task("Open Contenders")
                    .Run(async _ => await lockHolderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));

                scope.Task("Older Branch")
                    .After(contenderGate.Id)
                    .Children(ExecutionChildMode.Parallel, branch =>
                    {
                        /* This contender reaches the lock first and should receive credit for fanout unlocked after its
                           install/prebuild chain completes the shared-base parent scope. */
                        ExecutionTaskBuilder olderPackage = branch.Task("Older Package")
                            .WithExecutionLocks(sharedLock)
                            .Run(async context =>
                            {
                                lockWinner.TrySetResult(context.TaskId);
                                await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                            }, out olderPackageTaskId);

                        ExecutionTaskBuilder olderSharedBase = branch.Task("Older Shared Base");
                        olderSharedBase.Children(shared =>
                        {
                            ExecutionTaskBuilder install = shared.Task("Install")
                                .After(olderPackage.Id)
                                .Run(() => Task.CompletedTask);

                            shared.Task("Prebuild")
                                .After(install.Id)
                                .Run(() => Task.CompletedTask);
                        });

                        branch.Task("Older Fanout 1").After(olderSharedBase.Id).Run(() => Task.CompletedTask);
                        branch.Task("Older Fanout 2").After(olderSharedBase.Id).Run(() => Task.CompletedTask);
                        branch.Task("Older Fanout 3").After(olderSharedBase.Id).Run(() => Task.CompletedTask);
                    });

                scope.Task("Later Branch")
                    .After(contenderGate.Id)
                    .Children(ExecutionChildMode.Parallel, branch =>
                    {
                        /* Gate this branch so the older contender is already registered as a waiter before the later
                           contender becomes eligible for the same lock. */
                        ExecutionTaskBuilder delay = branch.Task("Delay Later Branch")
                            .Run(async _ => await allowLaterBranch.Task.WaitAsync(TimeSpan.FromSeconds(5)));

                        ExecutionTaskBuilder laterSharedBase = branch.Task("Later Shared Base")
                            .After(delay.Id);
                        laterSharedBase.Children(shared =>
                        {
                            ExecutionTaskBuilder install = shared.Task("Install")
                                .Run(() => Task.CompletedTask);

                            shared.Task("Later Build")
                                .After(install.Id)
                                .WithExecutionLocks(sharedLock)
                                .Run(async context =>
                                {
                                    lockWinner.TrySetResult(context.TaskId);
                                    await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                }, out laterBuildTaskId);
                        });

                        branch.Task("Later Fanout 1").After(laterSharedBase.Id).Run(() => Task.CompletedTask);
                        branch.Task("Later Fanout 2").After(laterSharedBase.Id).Run(() => Task.CompletedTask);
                        branch.Task("Later Fanout 3").After(laterSharedBase.Id).Run(() => Task.CompletedTask);
                    });
            });
        });

        // Wait for both contenders to be registered as lock waiters, then release the holder and observe the first winner.
        (ExecutionPlan _, ExecutionSession session, ExecutionPlanScheduler scheduler) = RuntimeTestUtilities.CreateRuntime(operation);
        Task<OperationResult> executeTask = scheduler.ExecuteAsync(CancellationToken.None);
        try
        {
            await RuntimeTestUtilities.WaitForConditionAsync(
                () => olderPackageTaskId != default
                    && session.Tasks.Any(task => task.Id == olderPackageTaskId)
                    && session.GetTask(olderPackageTaskId).State == ExecutionTaskState.WaitingForExecutionLock,
                TimeSpan.FromSeconds(5),
                "Timed out waiting for the older package-like contender to wait for the shared lock.");

            allowLaterBranch.TrySetResult(true);
            await RuntimeTestUtilities.WaitForConditionAsync(
                () => laterBuildTaskId != default
                    && session.Tasks.Any(task => task.Id == laterBuildTaskId)
                    && session.GetTask(laterBuildTaskId).State == ExecutionTaskState.WaitingForExecutionLock,
                TimeSpan.FromSeconds(5),
                "Timed out waiting for the later build-like contender to wait for the shared lock.");

            releaseLockHolder.TrySetResult(true);
            ExecutionTaskId winnerTaskId = await lockWinner.Task.WaitAsync(TimeSpan.FromSeconds(1));

            // The older package-like contender should win because it unlocks the parent-scope fanout that follows prebuild.
            Assert.Equal(olderPackageTaskId, winnerTaskId);
        }
        finally
        {
            allowLaterBranch.TrySetResult(true);
            releaseLockHolder.TrySetResult(true);
            releaseWinner.TrySetResult(true);
            await executeTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
