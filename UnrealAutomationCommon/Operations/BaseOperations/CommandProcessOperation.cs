using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnrealAutomationCommon.Operations;
#nullable enable

namespace UnrealAutomationCommon.Operations.BaseOperations
{
    /// <summary>
    /// Base operation for Unreal tool invocations that run one external command as the operation body.
    /// </summary>
    public abstract class CommandProcessOperation<T> : UnrealOperation<T> where T : global::LocalAutomation.Runtime.OperationTarget
    {
        /// <summary>
        /// Builds the concrete process command for the validated operation parameters.
        /// </summary>
        protected abstract global::LocalAutomation.Runtime.Command BuildCommand(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters);

        // Derived operations can inspect output as it streams without retaining the full process log in memory.
        protected virtual void OnOutputLine(string line)
        {
        }

        protected override IEnumerable<global::LocalAutomation.Runtime.Command> BuildCommands(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            return new List<global::LocalAutomation.Runtime.Command> { BuildCommand(operationParameters) };
        }

        /// <summary>
        /// Authors this operation as one process-backed task.
        /// </summary>
        protected override void DescribeExecutionPlan(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, global::LocalAutomation.Runtime.ExecutionTaskBuilder root)
        {
            root.Run(ExecuteProcessAsync);
        }

        /// <summary>
        /// Executes the configured command and allows derived operations to inspect the final result.
        /// </summary>
        protected async Task<global::LocalAutomation.Runtime.OperationResult> ExecuteProcessAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters = context.ValidatedOperationParameters;
            global::LocalAutomation.Runtime.Command command;
            using (global::LocalAutomation.Core.PerformanceActivityScope buildCommandActivity = global::LocalAutomation.Core.PerformanceTelemetry.StartActivity("CommandProcessOperation.BuildCommand"))
            {
                command = BuildCommand(operationParameters);
                buildCommandActivity.SetTag("command.file", command.File)
                    .SetTag("command.has_arguments", !string.IsNullOrWhiteSpace(command.Arguments));
            }

            global::LocalAutomation.Runtime.OperationResult result = await CommandProcessExecutor.ExecuteAsync(context, command, GetType().Name, OnOutputLine).ConfigureAwait(false);
            OnProcessEnded(context, operationParameters, result);
            return result;
        }

        /// <summary>
        /// Lets derived operations validate or adjust the result after the process exits.
        /// </summary>
        protected virtual void OnProcessEnded(global::LocalAutomation.Runtime.ExecutionTaskContext context, global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, global::LocalAutomation.Runtime.OperationResult result)
        {
        }
    }
}
