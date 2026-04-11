using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using TestUtilities;

namespace LocalAutomation.Runtime.Tests;

internal static class RuntimeTestUtilities
{
    /// <summary>
    /// Wraps the shared execution-test inline operation with the runtime test suite's fixed default operation name.
    /// </summary>
    internal sealed class InlineOperation : ExecutionTestCommon.InlineOperation
    {
        public InlineOperation(Action<ExecutionTaskBuilder> buildPlan)
            : base(buildPlan, "Scheduler Test Operation", null)
        {
        }
    }

    /// <summary>
    /// Builds a runtime execution plan through the same public operation pipeline the application uses.
    /// </summary>
    public static ExecutionPlan BuildPlan(Operation operation)
    {
        OperationParameters parameters = operation.CreateParameters();
        parameters.Target = new ExecutionTestCommon.TestTarget();
        return ExecutionPlanFactory.BuildPlan(operation, parameters)
            ?? throw new InvalidOperationException("The test operation did not produce an execution plan.");
    }

    /// <summary>
    /// Creates the live session and scheduler pair used by the integration-style runtime tests.
    /// </summary>
    public static ExecutionPlanScheduler CreateScheduler(ExecutionPlan plan, out ExecutionSession session)
    {
        session = new ExecutionSession(new BufferedLogStream(), plan);
        return new ExecutionPlanScheduler(TestLoggingBootstrap.LoggerFactory.CreateLogger("LocalAutomation.Runtime.Scheduler"), session);
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
        OperationResult result = await scheduler.ExecuteAsync(cancellationToken).ConfigureAwait(false);
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
    /// Waits until the supplied condition becomes true or throws after the provided timeout.
    /// </summary>
    public static async Task WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout, string timeoutMessage = "Timed out waiting for the expected runtime condition.")
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(timeoutMessage);
    }

}
