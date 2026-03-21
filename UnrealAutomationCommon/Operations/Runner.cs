using LocalAutomation.Core;
using Microsoft.Extensions.Logging;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Operations.BaseOperations;

#nullable enable

namespace UnrealAutomationCommon.Operations;

/// <summary>
/// Preserves the existing Unreal runner surface while delegating generic coordination to the shared runtime runner.
/// </summary>
public class Runner : global::LocalAutomation.Runtime.Runner
{
    /// <summary>
    /// Creates a runner for the provided Unreal operation and parameter state.
    /// </summary>
    public Runner(Operation operation, OperationParameters operationParameters)
        : base(operation, operationParameters, deleteDirectoryIfExists: FileUtils.DeleteDirectoryIfExists, beforeRun: WarnForWaitForAttach)
    {
    }

    /// <summary>
    /// Emits the legacy WaitForAttach reminder before execution starts.
    /// </summary>
    private static void WarnForWaitForAttach(global::LocalAutomation.Runtime.OperationParameters parameters, ILogger logger)
    {
        OperationParameters typedParameters = (OperationParameters)parameters;
        FlagOptions? flagOptions = typedParameters.FindOptions<FlagOptions>();
        if (flagOptions is { WaitForAttach: true })
        {
            logger.LogInformation("-WaitForAttach was specified, attach now");
        }
    }
}
