using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalAutomation.Runtime.Tests;

internal static class RuntimeTestUtilities
{
    /// <summary>
    /// Builds a runtime execution plan through the same public operation pipeline the application uses.
    /// </summary>
    public static ExecutionPlan BuildPlan(Operation operation)
    {
        OperationParameters parameters = operation.CreateParameters();
        parameters.Target = new TestTarget();
        return Runner.BuildPlan(operation, parameters)
            ?? throw new InvalidOperationException("The test operation did not produce an execution plan.");
    }

    /// <summary>
    /// Creates the live session and scheduler pair used by the integration-style runtime tests.
    /// </summary>
    public static ExecutionPlanScheduler CreateScheduler(ExecutionPlan plan, out ExecutionSession session)
    {
        session = new ExecutionSession(new BufferedLogStream(), plan: plan);
        return new ExecutionPlanScheduler(NullLogger.Instance, session);
    }

    /// <summary>
    /// Builds the authored plan and creates the live session/scheduler pair in one step for tests that still need access
    /// to the plan before execution starts.
    /// </summary>
    public static (ExecutionPlan plan, ExecutionSession session, ExecutionPlanScheduler scheduler) CreateRuntime(Operation operation)
    {
        ExecutionPlan plan = BuildPlan(operation);
        ExecutionPlanScheduler scheduler = CreateScheduler(plan, out ExecutionSession session);
        return (plan, session, scheduler);
    }

    /// <summary>
    /// Executes one operation through the real runtime pipeline and returns the authored plan plus the live session state
    /// so tests can assert on both authored and runtime task identities.
    /// </summary>
    public static async Task<(ExecutionPlan plan, ExecutionSession session, OperationResult result)> ExecuteAsync(
        Operation operation,
        CancellationToken cancellationToken = default)
    {
        (ExecutionPlan plan, ExecutionSession session, ExecutionPlanScheduler scheduler) = CreateRuntime(operation);
        OperationResult result = await scheduler.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
        return (plan, session, result);
    }

    /// <summary>
    /// Provides a reusable task body for active work that should remain running until the scheduler cancels it.
    /// </summary>
    public static async Task<OperationResult> RunUntilCancelled(ExecutionTaskContext context)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            return OperationResult.Succeeded();
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled();
        }
    }

    /// <summary>
    /// Creates one task body that signals when it starts, then stays active until the provided gate allows it to finish.
    /// This keeps scheduler tests deterministic when they need one body task to hold a lock or dependency boundary open.
    /// </summary>
    public static Func<ExecutionTaskContext, Task<OperationResult>> RunUntilReleased(TaskCompletionSource<bool> started, TaskCompletionSource<bool> release)
    {
        return async _ =>
        {
            started.TrySetResult(true);
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return OperationResult.Succeeded();
        };
    }

    /// <summary>
     /// Minimal inline operation wrapper that lets tests define plan shape through the normal runtime operation pipeline.
     /// </summary>
    internal class InlineOperation : Operation<TestTarget>
    {
        private readonly Action<ExecutionTaskBuilder> _buildPlan;
        private readonly IReadOnlyList<ExecutionLock> _executionLocks;

        public InlineOperation(Action<ExecutionTaskBuilder> buildPlan, params ExecutionLock[] executionLocks)
        {
            _buildPlan = buildPlan;
            _executionLocks = executionLocks ?? Array.Empty<ExecutionLock>();
        }

        /// <summary>
        /// Keeps the generated task paths stable across tests.
        /// </summary>
        protected override string GetOperationName()
        {
            return "Scheduler Test Operation";
        }

        /// <summary>
        /// Delegates plan construction to the test-provided builder action.
        /// </summary>
        protected override void DescribeExecutionPlan(ValidatedOperationParameters operationParameters, ExecutionTaskBuilder root)
        {
            _buildPlan(root);
        }

        /// <summary>
        /// Declares the same execution locks for every task body in this test operation so scheduler tests can focus on
        /// graph semantics instead of duplicating lock plumbing across helper types.
        /// </summary>
        protected override IEnumerable<ExecutionLock> GetExecutionLocks(ValidatedOperationParameters operationParameters)
        {
            return _executionLocks;
        }
    }

    /// <summary>
    /// Minimal valid target used by the runtime plan builder in tests.
    /// </summary>
    internal sealed class TestTarget : OperationTarget
    {
        public TestTarget()
        {
            TargetPath = AppContext.BaseDirectory;
        }

        /// <summary>
        /// Uses one stable display name for deterministic task paths in the tests.
        /// </summary>
        public override string Name => "TestTarget";

        /// <summary>
        /// The test target has no descriptor-backed state to load.
        /// </summary>
        public override void LoadDescriptor()
        {
        }
    }
}
