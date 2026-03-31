using LocalAutomation.Core;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Runtime;

/// <summary>
/// Creates loggers that route output into a specific execution-plan task while preserving wider session/application fan-out.
/// </summary>
public interface IExecutionTaskLoggerFactory
{
    ILogger CreateTaskLogger(ExecutionTaskId taskId);
}
